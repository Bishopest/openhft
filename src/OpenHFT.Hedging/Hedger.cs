using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Hedging;

public class Hedger
{
    private readonly ILogger _logger;
    private readonly Instrument _quoteInstrument;
    private readonly Instrument _hedgeInstrument;
    private readonly HedgingParameters _hedgeParameters;
    private readonly IOrderFactory _orderFactory; // To create IOrder objects
    private readonly string _bookName;
    private readonly IMarketDataManager _marketDataManager;
    private readonly IFxRateService _fxRateManager;
    private OrderBook? _cachedOrderBook;
    private OrderBook? _cachedReferenceOrderBook;
    private IOrder? _hedgeOrder = null!;

    private readonly object _pendingValueLock = new();
    private readonly object _stateLock = new();

    private readonly object _targetLock = new();
    // remaining value in denomination currency
    // if (+) then (Buy) ,otherwise (Sell)
    private Quantity _netPendingHedgeQuantity = Quantity.FromDecimal(0);
    private bool _isActive = false;
    private static readonly HashSet<string> SupportedCurrencies = new() { "BTC", "USDT" };

    public bool IsActive => _isActive;
    public HedgingParameters HedgeParameters => _hedgeParameters;

    public Hedger(
        ILogger<Hedger> logger,
        Instrument quoteInstrument,
        Instrument hedgeInstrument,
        HedgingParameters hedgingParameters,
        IOrderFactory orderFactory,
        string bookName,
        IMarketDataManager marketDataManager,
        IFxRateService fxRateManager
    )
    {
        _logger = logger;
        _quoteInstrument = quoteInstrument;
        _hedgeInstrument = hedgeInstrument;
        _hedgeParameters = hedgingParameters;
        _orderFactory = orderFactory;
        _bookName = bookName;
        _marketDataManager = marketDataManager;
        _fxRateManager = fxRateManager;
    }

    public void Activate()
    {
        if (!IsValidCurrency(_quoteInstrument.DenominationCurrency) ||
            !IsValidCurrency(_hedgeInstrument.DenominationCurrency))
        {
            _logger.LogWarningWithCaller($"Unsupported denom currency quote({_quoteInstrument.DenominationCurrency}) or hedge({_hedgeInstrument.DenominationCurrency}) detected. Only BTC and USDT are supported.");
            return;
        }

        if (_quoteInstrument.BaseCurrency != _hedgeInstrument.BaseCurrency)
        {
            _logger.LogWarningWithCaller($"quoting base {_quoteInstrument.BaseCurrency} is not equals to hedge base {_hedgeInstrument.BaseCurrency}, skipping");
            return;
        }

        _isActive = true;
        _logger.LogInformationWithCaller($"Starting Hedger for Q:{_quoteInstrument.Symbol} H:{_hedgeInstrument.Symbol}.");
        var subscriptionKey = $"Hedger_{_quoteInstrument.Symbol}_{_hedgeInstrument.Symbol}_{_bookName}";
        _marketDataManager.SubscribeOrderBook(_hedgeInstrument.InstrumentId, subscriptionKey, (sender, book) => UpdateHedgeOrderBook(book));
    }

    public void Deactivate()
    {
        if (!_isActive)
        {
            _logger.LogWarningWithCaller($"Can not deactivate non-active hedger on quoting instrument {_quoteInstrument.Symbol}, hedging instrument {_hedgeInstrument.Symbol} on book {_bookName}");
            return;
        }
        _isActive = false;

        _logger.LogInformationWithCaller($"Stop Hedger for Q:{_quoteInstrument.Symbol} H:{_hedgeInstrument.Symbol}.");
        var subscriptionKey = $"Hedger_{_quoteInstrument.Symbol}_{_hedgeInstrument.Symbol}_{_bookName}";
        _marketDataManager.UnsubscribeOrderBook(_hedgeInstrument.InstrumentId, subscriptionKey);

        if (_hedgeOrder is not null)
        {
            _ = _hedgeOrder.CancelAsync(CancellationToken.None);
        }
    }

    private bool IsValidCurrency(Currency c) => SupportedCurrencies.Contains(c.Symbol.ToUpper());

    public void UpdateHedgeOrderBook(OrderBook ob)
    {
        if (ob.InstrumentId != _hedgeInstrument.InstrumentId)
        {
            return;
        }

        _cachedOrderBook = ob;
        var target = GetHedgeQuote();
        _ = ExecuteQuoteUpdateAsync(target);
    }

    public void UpdateQuotingFill(Fill fill)
    {
        if (fill.InstrumentId != _quoteInstrument.InstrumentId)
        {
            return;
        }

        try
        {
            var fillValueAmount = _quoteInstrument.ValueInDenominationCurrency(fill.Price, fill.Quantity);
            var hedgeValueAmount = fill.Side == Side.Buy ? fillValueAmount * -1m : fillValueAmount;

            lock (_pendingValueLock)
            {
                var targetCurrencyValue = _fxRateManager.Convert(hedgeValueAmount, _hedgeInstrument.DenominationCurrency);
                if (targetCurrencyValue == null)
                {
                    _logger.LogWarningWithCaller("Failed to convert currency. Skipping hedge accumulation.");
                    return;
                }

                // 4. Hedge Instrument 수량 환산 (Reference Price or Hedge Price 사용)
                // 정확한 수량 계산을 위해 Hedge Instrument의 현재가(Mid)를 사용하는 것이 가장 정확함
                if (_cachedOrderBook == null)
                {
                    _logger.LogWarningWithCaller("Hedge orderbook not ready. Skipping hedge accumulation.");
                    return;
                }

                var midPrice = _cachedOrderBook.GetMidPrice();
                var targetQuantity = CalculateQuantityFromValue(_hedgeInstrument, midPrice, targetCurrencyValue.Value.Amount);

                _netPendingHedgeQuantity += targetQuantity;
            }

            _logger.LogInformationWithCaller($"Fill received: {fill}. Pending Hedge Value accumulated to: {_netPendingHedgeQuantity}");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error inside UpdateQuotingFill in Hedger.");
        }
    }

    public void OnHedgingFill(object? sender, Fill fill)
    {
        if (fill.InstrumentId != _hedgeInstrument.InstrumentId)
        {
            return;
        }

        try
        {
            var singedHedgeQuantityDecimal = fill.Side == Side.Buy ? fill.Quantity.ToDecimal() * -1m : fill.Quantity.ToDecimal();

            lock (_pendingValueLock)
            {
                _netPendingHedgeQuantity += Quantity.FromDecimal(singedHedgeQuantityDecimal);
            }

            _logger.LogInformationWithCaller($"Fill received: {fill}. Pending Hedge Value accumulated to: {_netPendingHedgeQuantity}");

            // in case of ws execution message arrives later
            if (sender is not null
                && sender is IOrder order
                && order.Status == OrderStatus.Filled
                && order.LatestReport is not null)
            {
                ClearActiveOrder(order.LatestReport.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error inside UpdateQuotingFill in Hedger.");
        }
    }

    private void OnOrderStatusChanged(object? sender, OrderStatusReport report)
    {
        // Check if the order has reached a terminal state
        switch (report.Status)
        {
            case OrderStatus.Cancelled:
            case OrderStatus.Rejected:
                ClearActiveOrder(report);
                break;
        }
    }

    private SideQuote? GetHedgeQuote()
    {
        decimal pendingQtyDecimal;
        lock (_pendingValueLock)
        {
            pendingQtyDecimal = _netPendingHedgeQuantity.ToDecimal();
        }

        if (_hedgeParameters.Size.ToDecimal() <= 0m
            || Math.Abs(pendingQtyDecimal) < _hedgeInstrument.MinOrderSize.ToDecimal())
        {
            return null;
        }

        var sideToHedge = _netPendingHedgeQuantity.ToDecimal() > 0 ? Side.Buy : Side.Sell;
        var absQty = Math.Abs(pendingQtyDecimal);
        var maxSliceSize = _hedgeParameters.Size.ToDecimal();
        var qtyToOrderDec = absQty > maxSliceSize ? maxSliceSize : absQty;
        var lotSize = _hedgeInstrument.LotSize.ToDecimal();
        qtyToOrderDec = Math.Floor(qtyToOrderDec / lotSize) * lotSize;

        if (qtyToOrderDec < _hedgeInstrument.MinOrderSize.ToDecimal()) return null;

        if (_cachedOrderBook == null) return null;

        var (bestBid, _) = _cachedOrderBook.GetBestBid();
        var (bestAsk, _) = _cachedOrderBook.GetBestAsk();

        if (bestBid.ToDecimal() <= 0 || bestAsk.ToDecimal() <= 0) return null;

        var qtyToOrder = Quantity.FromDecimal(qtyToOrderDec);
        var tick = _hedgeInstrument.TickSize.ToDecimal();

        if (_hedgeParameters.OrderType == HedgeOrderType.OppositeFirst)
        {
            // Taker: Buy -> Ask, Sell -> Bid
            if (sideToHedge == Side.Buy)
            {
                Price oppositeFirstMarketPrice = bestAsk;
                Price myPrice = _hedgeOrder is null ? bestAsk : _hedgeOrder.Price;
                decimal properPriceDecimal = Math.Min(oppositeFirstMarketPrice.ToDecimal(), myPrice.ToDecimal());
                return new SideQuote(sideToHedge, Price.FromDecimal(properPriceDecimal), qtyToOrder);
            }
            else
            {
                Price oppositeFirstMarketPrice = bestBid;
                Price myPrice = _hedgeOrder is null ? bestBid : _hedgeOrder.Price;
                decimal properPriceDecimal = Math.Max(oppositeFirstMarketPrice.ToDecimal(), myPrice.ToDecimal());
                return new SideQuote(sideToHedge, Price.FromDecimal(properPriceDecimal), qtyToOrder);
            }
        }
        else // FirstFollow (Maker)
        {
            if (sideToHedge == Side.Buy)
            {
                Price ourSideFirstMarketPrice = bestBid;
                Price myPrice = _hedgeOrder is null ? bestBid : _hedgeOrder.Price;
                if (ourSideFirstMarketPrice > myPrice)
                {
                    var properPrice = Price.FromDecimal(ourSideFirstMarketPrice.ToDecimal() + tick);
                    return new SideQuote(sideToHedge, properPrice, qtyToOrder);
                }
                else
                {
                    return new SideQuote(sideToHedge, myPrice, qtyToOrder);
                }
            }
            else
            {
                Price ourSideFirstMarketPrice = bestAsk;
                Price myPrice = _hedgeOrder is null ? bestAsk : _hedgeOrder.Price;
                if (ourSideFirstMarketPrice < myPrice)
                {
                    var properPrice = Price.FromDecimal(ourSideFirstMarketPrice.ToDecimal() - tick);
                    return new SideQuote(sideToHedge, properPrice, qtyToOrder);
                }
                else
                {
                    return new SideQuote(sideToHedge, myPrice, qtyToOrder);
                }
            }
        }
    }

    public async Task ExecuteQuoteUpdateAsync(SideQuote? target)
    {
        IOrder? currentOrder;
        lock (_stateLock)
        {
            currentOrder = _hedgeOrder;
        }

        if (target is null)
        {
            if (currentOrder is not null)
            {
                await currentOrder.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        if (currentOrder == null)
        {
            await StartNewHedgeAsync(target.Value);
        }
        else
        {
            if (target.Value.Price != currentOrder.Price)
            {
                await currentOrder.ReplaceAsync(target.Value.Price, OrderType.Limit, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task StartNewHedgeAsync(SideQuote target)
    {
        var orderBuilder = new OrderBuilder(_orderFactory, _hedgeInstrument.InstrumentId, target.Side, _bookName);
        var newOrder = orderBuilder
            .WithPrice(target.Price)
            .WithQuantity(target.Size)
            .WithOrderType(OrderType.Limit)
            .WithStatusChangedHandler(OnOrderStatusChanged)
            .WithFillHandler(OnHedgingFill)
            .Build();

        lock (_stateLock)
        {
            // Ensure there isn't already an active order (race condition check)
            if (_hedgeOrder is not null)
            {
                _logger.LogWarningWithCaller($"({target.Side}) Aborting StartNewHedgeAsync; an hedge order was created concurrently.");
                newOrder.RemoveStatusChangedHandler(OnOrderStatusChanged);
                newOrder.RemoveFillHandler(OnHedgingFill);
                return;
            }

            _hedgeOrder = newOrder;
        }

        _logger.LogInformationWithCaller($"({target.Side}) Submitting new order {newOrder.ClientOrderId} for hedge: {target}");
        await newOrder.SubmitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private Quantity CalculateQuantityFromValue(Instrument instrument, Price price, decimal valueToHedge)
    {
        if (valueToHedge == 0m)
        {
            return Quantity.FromDecimal(0m);
        }

        var unitValue = instrument.ValueInDenominationCurrency(price, Quantity.FromDecimal(1m));
        if (unitValue.Amount <= 0m) return Quantity.FromDecimal(0m);

        decimal rawQuantityDecimal = valueToHedge / unitValue.Amount;

        var minOrderSizeTicks = instrument.MinOrderSize.ToTicks();
        if (minOrderSizeTicks <= 0) return Quantity.FromDecimal(0m);

        return Quantity.FromDecimal(rawQuantityDecimal);
    }

    private void ClearActiveOrder(OrderStatusReport finalReport)
    {
        lock (_stateLock)
        {
            if (_hedgeOrder is null || _hedgeOrder.ClientOrderId != finalReport.ClientOrderId)
            {
                // This is a late message for an old, already cleared order. Ignore.
                return;
            }

            _logger.LogInformationWithCaller($"({_hedgeOrder.Side}) Order {_hedgeOrder.ClientOrderId} reached terminal state {finalReport.Status}. Clearing hedge order.");

            // fully filled process
            if (finalReport.Status == OrderStatus.Filled)
            {
                _logger.LogInformationWithCaller($"Hedge order {finalReport.ClientOrderId} has been fully filled.");
            }

            _hedgeOrder = null;
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_hedgeOrder != null)
            {
                _hedgeOrder.RemoveStatusChangedHandler(OnOrderStatusChanged);
                _hedgeOrder.RemoveFillHandler(OnHedgingFill);           // Unsubscribe to prevent memory leaks
                _hedgeOrder = null; // Or consider sending a final cancel request.
            }
            Deactivate();
        }
    }
}
