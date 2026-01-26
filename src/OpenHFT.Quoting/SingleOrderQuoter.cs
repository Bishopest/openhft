using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

public class SingleOrderQuoter : IQuoter
{
    private readonly ILogger _logger;
    private readonly Side _side;
    private readonly Instrument _instrument;
    private readonly IOrderFactory _orderFactory; // To create IOrder objects
    private readonly IMarketDataManager _marketDataManager;
    private readonly string _bookName;
    private readonly object _stateLock = new();
    private IOrder? _activeOrder;

    // [추가] Replace 실패(취소) 시 즉시 재전송할 쿼트 정보 저장
    private Quote? _pendingReentryQuote;
    private bool _pendingReentryPostOnly;
    /// <summary>
    /// The most recent quote that was requested to be updated.
    /// Null if the last action was a cancellation.
    /// </summary>
    public Quote? LatestQuote { get; private set; }
    private HittingLogic _hittingLogic = HittingLogic.AllowAll;
    private OrderBook? _cachedOrderBook;
    public event Action? OrderFullyFilled;
    public event Action<Fill>? OrderFilled;

    public SingleOrderQuoter(
            ILogger logger,
            Side side,
            Instrument instrument,
            IOrderFactory orderFactory,
            string bookName,
            IMarketDataManager marketDataManager)
    {
        _logger = logger;
        _side = side;
        _instrument = instrument;
        _orderFactory = orderFactory;
        _bookName = bookName;
        _marketDataManager = marketDataManager;
    }

    private OrderBook? GetOrderBookFast()
    {
        if (_cachedOrderBook != null)
        {
            return _cachedOrderBook;
        }

        var book = _marketDataManager.GetOrderBook(_instrument.InstrumentId);
        if (book != null)
        {
            _cachedOrderBook = book; // 찾았으면 캐싱
        }

        return _cachedOrderBook;
    }

    public void UpdateParameters(QuotingParameters parameters)
    {
        _hittingLogic = parameters.HittingLogic;
    }

    public async Task UpdateQuoteAsync(Quote newQuote, bool isPostOnly, Quantity? availablePosition, CancellationToken cancellationToken = default)
    {
        try
        {
            var finalQuote = newQuote;

            if (_side == Side.Sell && _instrument.ProductType == ProductType.Spot)
            {
                var targetSize = newQuote.Size;
                var availableSize = availablePosition ?? Quantity.Zero;

                // Use the smaller of the two: the requested size or the available balance.
                var finalSize = targetSize > availableSize ? availableSize : targetSize;

                if (finalSize.ToDecimal() < _instrument.MinOrderSize.ToDecimal())
                {
                    _logger.LogDebug("({Side}) Adjusted sell size {Size} is below min order size. Cancelling any active quote.", _side, finalSize);
                    await CancelQuoteAsync(cancellationToken);
                    return;
                }

                if (finalSize != targetSize)
                {
                    _logger.LogDebug("({Side}) Limiting Spot Sell order size from {TargetSize} to available position {AvailableSize}.",
                        _side, targetSize, finalSize);
                }

                finalQuote = new Quote(newQuote.Price, finalSize);
            }

            var effectivePrice = ApplyHittingLogic(finalQuote.Price);
            var effectiveQuote = effectivePrice == finalQuote.Price
                ? finalQuote
                : new Quote(effectivePrice, finalQuote.Size);

            LatestQuote = effectiveQuote;

            IOrder? currentOrder;
            lock (_stateLock)
            {
                currentOrder = _activeOrder;
                _pendingReentryQuote = null;
            }

            if (currentOrder is null)
            {
                // No active order, so place a new one.
                await StartNewQuoteAsync(effectiveQuote, isPostOnly, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (effectiveQuote.Price != currentOrder.Price)
                {
                    bool isPartiallyFilled = currentOrder.Quantity > currentOrder.LeavesQuantity;
                    var book = GetOrderBookFast();
                    bool isNearMid = false;
                    if (book is not null)
                    {
                        var mid = book.GetMidPrice();
                        var upperPrice = mid.ToDecimal() * (1m + 3m * 1e-4m);
                        var lowerPrice = mid.ToDecimal() * (1m - 3m * 1e-4m);
                        isNearMid = effectiveQuote.Price.ToDecimal() >= lowerPrice && effectiveQuote.Price.ToDecimal() <= upperPrice;
                    }

                    if (isPartiallyFilled && !isNearMid)
                    {
                        await currentOrder.CancelAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        if (currentOrder.SupportsOrderReplacement)
                        {
                            await currentOrder.ReplaceAsync(effectiveQuote.Price, OrderType.Limit, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            lock (_stateLock)
                            {
                                _pendingReentryQuote = effectiveQuote;
                                _pendingReentryPostOnly = isPostOnly;
                            }

                            await currentOrder.CancelAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"({_side}) An unexpected error occurred during UpdateQuoteAsync.");
        }
    }

    /// <summary>
    /// adjust quote price by HittingLogic.
    /// </summary>
    private Price ApplyHittingLogic(Price targetPrice)
    {
        if (_hittingLogic == HittingLogic.AllowAll)
        {
            return targetPrice;
        }

        var book = GetOrderBookFast();
        if (book == null) return targetPrice; // 오더북 없으면 로직 적용 불가, 원본 반환

        var bestBid = book.GetBestBid().price;
        var bestAsk = book.GetBestAsk().price;

        // 유효성 체크
        if (bestBid.ToDecimal() <= 0 || bestAsk.ToDecimal() <= 0) return targetPrice;

        var tickSize = _instrument.TickSize.ToDecimal();

        if (_side == Side.Buy)
        {
            // Buy Side Logic
            if (_hittingLogic == HittingLogic.OurBest)
            {
                // "prevent quoter to quote price over best price"
                // Buy 주문이 Best Bid보다 높게 나가는 것을 방지 (Taker 방지보다는 Passive 유지 목적)
                // 수정: OurBest = "Best Bid 까지만 허용" (즉, Spread 안쪽으로 들어가지 않음)

                if (targetPrice.ToDecimal() > bestBid.ToDecimal())
                {
                    return bestBid;
                }
            }
            else if (_hittingLogic == HittingLogic.Pennying)
            {

                if (targetPrice.ToDecimal() > bestBid.ToDecimal())
                {
                    // "quote price just +1 tick from best price ... only when our quote cross the best price"
                    // 목표가가 Best Bid보다 높다면 -> Best Bid + 1 Tick으로 제한 (Pennying)
                    // 목표가가 Best Bid 이하라면 -> 그냥 목표가 사용
                    if (_activeOrder is not null && _activeOrder.Price == bestBid)
                    {
                        return bestBid;
                    }

                    // Best Bid + 1 Tick (단, Best Ask를 넘지 않도록 주의해야 Taker 방지됨)
                    var pennyPrice = bestBid.ToDecimal() + tickSize;

                    // (옵션) Best Ask와 겹치거나 넘어가면 Best Ask - 1 Tick 등으로 조정할 수도 있음 (PostOnly 보장 위해)
                    if (pennyPrice >= bestAsk.ToDecimal())
                    {
                        // Spread가 1틱인 경우 Pennying 불가 -> Best Bid 유지 or Best Ask - 1 tick
                        return bestBid;
                    }

                    return Price.FromDecimal(pennyPrice);
                }
            }
        }
        else // Sell Side
        {
            // Sell Side Logic
            if (_hittingLogic == HittingLogic.OurBest)
            {
                // Sell 주문이 Best Ask보다 낮게 나가는 것을 방지
                if (targetPrice.ToDecimal() < bestAsk.ToDecimal())
                {
                    return bestAsk;
                }
            }
            else if (_hittingLogic == HittingLogic.Pennying)
            {
                // 목표가가 Best Ask보다 낮다면 -> Best Ask - 1 Tick으로 제한
                if (targetPrice.ToDecimal() < bestAsk.ToDecimal())
                {
                    if (_activeOrder is not null && _activeOrder.Price == bestAsk)
                    {
                        return bestAsk;
                    }

                    var pennyPrice = bestAsk.ToDecimal() - tickSize;

                    // Best Bid와 겹치면 Pennying 불가
                    if (pennyPrice <= bestBid.ToDecimal())
                    {
                        return bestAsk;
                    }

                    return Price.FromDecimal(pennyPrice);
                }
            }
        }

        return targetPrice;
    }

    public async Task CancelQuoteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IOrder? orderToCancel;
            lock (_stateLock)
            {
                _pendingReentryQuote = null;
                LatestQuote = null;
                orderToCancel = _activeOrder;
            }

            if (orderToCancel is null)
            {
                _logger.LogDebug($"({_side}) No active quote to cancel.");
                return;
            }

            await orderToCancel.CancelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"({_side}) An unexpected error occurred during CancelQuoteAsync.");
        }
    }

    private async Task StartNewQuoteAsync(Quote quote, bool isPostOnly, CancellationToken cancellationToken)
    {
        // Use the factory and builder to create a new order object.
        // The builder logic is now encapsulated elsewhere.
        var orderBuilder = new OrderBuilder(_orderFactory, _instrument.InstrumentId, _side, _bookName, OrderSource.NonManual);
        var newOrder = orderBuilder
            .WithPrice(quote.Price)
            .WithQuantity(quote.Size)
            .WithOrderType(OrderType.Limit)
            .WithPostOnly(isPostOnly)
            .WithStatusChangedHandler(OnOrderStatusChanged)
            .WithFillHandler(OnOrderFilled)
            .Build();

        lock (_stateLock)
        {
            // Ensure there isn't already an active order (race condition check)
            if (_activeOrder is not null)
            {
                _logger.LogWarningWithCaller($"({_side}) Aborting StartNewQuoteAsync; an active order was created concurrently.");
                newOrder.RemoveStatusChangedHandler(OnOrderStatusChanged);
                newOrder.RemoveFillHandler(OnOrderFilled);
                return;
            }

            _activeOrder = newOrder;
        }

        _logger.LogDebug($"({_side}) Submitting new order {newOrder.ClientOrderId} for quote: {quote}");

        // Delegate the actual submission to the order object itself.
        await newOrder.SubmitAsync(cancellationToken).ConfigureAwait(false);
    }

    // This is the crucial feedback loop that listens to the order's state changes.
    private void OnOrderStatusChanged(object? sender, OrderStatusReport report)
    {
        // Check if the order has reached a terminal state
        switch (report.Status)
        {
            case OrderStatus.Filled:
            case OrderStatus.Cancelled:
            case OrderStatus.Rejected:
                ClearActiveOrder(report);

                Quote? reentryQuote = null;
                bool reentryPostOnly = false;

                lock (_stateLock)
                {
                    if (_pendingReentryQuote != null)
                    {
                        reentryQuote = _pendingReentryQuote;
                        reentryPostOnly = _pendingReentryPostOnly;
                        _pendingReentryQuote = null;
                    }
                }

                if (reentryQuote != null)
                {
                    _logger.LogDebug($"({_side}) Order {report.ClientOrderId} ended ({report.Status}). Triggering immediate reentry for {reentryQuote}.");
                    Task.Run(() => StartNewQuoteAsync(reentryQuote.Value, reentryPostOnly, CancellationToken.None));
                }

                break;
        }
    }

    private void OnOrderFilled(object? sender, Fill fill)
    {
        OrderFilled?.Invoke(fill);
    }

    private void ClearActiveOrder(OrderStatusReport finalReport)
    {
        lock (_stateLock)
        {
            if (_activeOrder is null || _activeOrder.ClientOrderId != finalReport.ClientOrderId)
            {
                // This is a late message for an old, already cleared order. Ignore.
                return;
            }

            _logger.LogDebug($"({_side}) Order {_activeOrder.ClientOrderId} reached terminal state {finalReport.Status}. Clearing active order.");

            // fully filled process
            if (finalReport.Status == OrderStatus.Filled)
            {
                _logger.LogInformationWithCaller($"Active order {finalReport.ClientOrderId} has been fully filled. Trigerring cooldown.");
                OrderFullyFilled?.Invoke();
            }

            // do not unsubscribe fill and status change event handler
            // because lazy update could possibily happen.
            // order router will finally reset event handlers with lazy deregister
            _activeOrder = null;
        }
    }

    public void Dispose()
    {
        // Ensure we unsubscribe from any active order events when the Quoter is disposed.
        lock (_stateLock)
        {
            if (_activeOrder != null)
            {
                _activeOrder.RemoveStatusChangedHandler(OnOrderStatusChanged);
                _activeOrder.RemoveFillHandler(OnOrderFilled);
                _activeOrder = null; // Or consider sending a final cancel request.
            }
        }
    }
}