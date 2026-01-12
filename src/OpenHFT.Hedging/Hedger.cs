using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;

namespace OpenHFT.Hedging;

public class Hedger : IDisposable
{
    private readonly ILogger _logger;
    private readonly Instrument _quoteInstrument;
    private readonly Instrument _hedgeInstrument;
    private readonly HedgingParameters _hedgeParameters;
    private readonly IOrderFactory _orderFactory;
    private readonly string _bookName;
    private readonly IMarketDataManager _marketDataManager;
    private readonly IFxRateService _fxRateManager;

    private OrderBook? _cachedOrderBook;

    // 활성 헤지 주문 (일반 Order 또는 AlgoOrder)
    private IOrder? _activeHedgeOrder;

    private readonly object _pendingValueLock = new();
    private readonly object _stateLock = new();

    // 헤지가 필요한 잔여 가치 (Denomination Currency 기준)
    // (+): 매수 필요, (-): 매도 필요
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
            _logger.LogWarningWithCaller($"Unsupported currency pair Q:{_quoteInstrument.DenominationCurrency}/H:{_hedgeInstrument.DenominationCurrency}. Only BTC/USDT supported.");
            return;
        }

        if (_quoteInstrument.BaseCurrency != _hedgeInstrument.BaseCurrency)
        {
            _logger.LogWarningWithCaller($"Base currency mismatch: Quote({_quoteInstrument.BaseCurrency}) != Hedge({_hedgeInstrument.BaseCurrency}). Skipping.");
            return;
        }

        _isActive = true;
        _logger.LogInformationWithCaller($"Starting Hedger for Q:{_quoteInstrument.Symbol} H:{_hedgeInstrument.Symbol}.");

        // 초기 진입 시점 판단을 위해 오더북 구독 (이후 AlgoOrder는 각자 구독함)
        var subscriptionKey = $"Hedger_{_quoteInstrument.Symbol}_{_hedgeInstrument.Symbol}_{_bookName}";
        _marketDataManager.SubscribeOrderBook(_hedgeInstrument.InstrumentId, subscriptionKey, (sender, book) => UpdateHedgeOrderBook(book));
    }

    public void Deactivate()
    {
        lock (_stateLock)
        {
            // 활성 주문이 있다면 취소 시도
            if (_activeHedgeOrder is not null)
            {
                try
                {
                    _ = _activeHedgeOrder.CancelAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogErrorWithCaller(ex, "Failed to cancel hedge order during deactivation.");
                }
            }
        }

        if (!_isActive) return;

        _isActive = false;
        _logger.LogInformationWithCaller($"Stop Hedger for Q:{_quoteInstrument.Symbol} H:{_hedgeInstrument.Symbol}.");

        var subscriptionKey = $"Hedger_{_quoteInstrument.Symbol}_{_hedgeInstrument.Symbol}_{_bookName}";
        _marketDataManager.UnsubscribeOrderBook(_hedgeInstrument.InstrumentId, subscriptionKey);
    }

    private bool IsValidCurrency(Currency c) => SupportedCurrencies.Contains(c.Symbol.ToUpper());

    // 시장 데이터 수신 핸들러
    public void UpdateHedgeOrderBook(OrderBook ob)
    {
        if (ob.InstrumentId != _hedgeInstrument.InstrumentId) return;

        _cachedOrderBook = ob;

        // 활성 주문이 없을 때만 신규 진입 여부를 체크
        // (주문이 있으면 AlgoOrder가 알아서 정정하거나, 일반 주문이면 대기)
        lock (_stateLock)
        {
            if (_activeHedgeOrder == null)
            {
                _ = CheckAndStartHedgeAsync();
            }
        }
    }

    // Quoting 체결 수신 (헤지 물량 발생)
    public void UpdateQuotingFill(Fill fill)
    {
        if (fill.InstrumentId != _quoteInstrument.InstrumentId) return;

        try
        {
            var fillValueAmount = _quoteInstrument.ValueInDenominationCurrency(fill.Price, fill.Quantity);
            // 매수 체결 -> 롱 포지션 증가 -> 헤지는 매도(-) 필요
            // 매도 체결 -> 숏 포지션 증가 -> 헤지는 매수(+) 필요
            var hedgeValueAmount = fill.Side == Side.Buy ? fillValueAmount * -1m : fillValueAmount;

            lock (_pendingValueLock)
            {
                var targetCurrencyValue = _fxRateManager.Convert(hedgeValueAmount, _hedgeInstrument.DenominationCurrency);
                if (targetCurrencyValue == null)
                {
                    _logger.LogWarningWithCaller("Failed to convert currency. Skipping hedge accumulation.");
                    return;
                }

                if (_cachedOrderBook == null)
                {
                    _logger.LogWarningWithCaller("Hedge orderbook not ready. Skipping hedge accumulation.");
                    return;
                }

                var midPrice = _cachedOrderBook.GetMidPrice();
                var targetQuantity = CalculateQuantityFromValue(_hedgeInstrument, midPrice, targetCurrencyValue.Value.Amount);

                _netPendingHedgeQuantity += targetQuantity;
            }

            _logger.LogInformationWithCaller($"Quoting Fill: {fill.Side} {fill.Quantity} @ {fill.Price}. Pending Hedge: {_netPendingHedgeQuantity}");

            // 물량 변동이 생겼으므로 즉시 진입 체크
            _ = CheckAndStartHedgeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error inside UpdateQuotingFill.");
        }
    }

    private async Task CheckAndStartHedgeAsync()
    {
        decimal pendingQtyDecimal;
        lock (_pendingValueLock)
        {
            pendingQtyDecimal = _netPendingHedgeQuantity.ToDecimal();
        }

        // 1. 헤지 조건 체크 (최소 수량 등)
        if (_hedgeParameters.Size.ToDecimal() <= 0m ||
            Math.Abs(pendingQtyDecimal) < _hedgeInstrument.MinOrderSize.ToDecimal())
        {
            return;
        }

        // 2. 중복 진입 방지
        lock (_stateLock)
        {
            if (_activeHedgeOrder != null) return;
        }

        var sideToHedge = pendingQtyDecimal > 0 ? Side.Buy : Side.Sell;
        var absQty = Math.Abs(pendingQtyDecimal);
        var maxSliceSize = _hedgeParameters.Size.ToDecimal();

        // 3. 주문 수량 결정 (Slice 적용)
        var qtyToOrderDec = absQty > maxSliceSize ? maxSliceSize : absQty;
        var lotSize = _hedgeInstrument.LotSize.ToDecimal();
        qtyToOrderDec = Math.Floor(qtyToOrderDec / lotSize) * lotSize;

        if (qtyToOrderDec < _hedgeInstrument.MinOrderSize.ToDecimal()) return;

        // 4. AlgoOrderType 결정
        var algoType = _hedgeParameters.OrderType switch
        {
            AlgoOrderType.OppositeFirst => AlgoOrderType.OppositeFirst,
            AlgoOrderType.FirstFollow => AlgoOrderType.FirstFollow,
            _ => AlgoOrderType.None
        };

        var qtyToOrder = Quantity.FromDecimal(qtyToOrderDec);

        // 5. 주문 실행
        await StartNewHedgeOrderAsync(sideToHedge, qtyToOrder, algoType);
    }

    private async Task StartNewHedgeOrderAsync(Side side, Quantity quantity, AlgoOrderType algoType)
    {
        // 1. 빌더를 통해 주문 생성 (가격은 AlgoOrder가 스스로 결정하므로 설정 안 함)
        var builder = new OrderBuilder(_orderFactory, _hedgeInstrument.InstrumentId, side, _bookName, OrderSource.NonManual, algoType);

        var newOrder = builder
            .WithQuantity(quantity)
            .WithOrderType(OrderType.Limit)
            .WithPostOnly(false)
            .WithStatusChangedHandler(OnOrderStatusChanged)
            .WithFillHandler(OnHedgingFill)
            .Build();

        lock (_stateLock)
        {
            if (_activeHedgeOrder != null) return; // Race condition 방어
            _activeHedgeOrder = newOrder;
        }

        decimal sign = side == Side.Buy ? 1 : -1;
        decimal qtyDec = quantity.ToDecimal();

        // 2. [Intention-based Accounting] 주문 제출 전, 체결된 것으로 가정하고 수량 차감
        lock (_pendingValueLock)
        {
            _netPendingHedgeQuantity -= Quantity.FromDecimal(qtyDec * sign);
        }

        _logger.LogInformationWithCaller($"({side}) Submitting Hedge {newOrder.ClientOrderId}: {quantity} (Algo: {algoType}). Remaining pending: {_netPendingHedgeQuantity}");

        try
        {
            // 3. 주문 전송 (여기서 AlgoOrder.SubmitAsync가 실행됨)
            await newOrder.SubmitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Failed to submit hedge order {newOrder.ClientOrderId}. Rolling back pending quantity.");

            // 4. [Rollback] 전송 실패 시 차감했던 수량 복구 및 주문 정리
            lock (_pendingValueLock)
            {
                _netPendingHedgeQuantity += Quantity.FromDecimal(qtyDec * sign);
            }

            lock (_stateLock)
            {
                if (_activeHedgeOrder == newOrder)
                {
                    if (_activeHedgeOrder is IDisposable disposable) disposable.Dispose();
                    _activeHedgeOrder = null;
                }
            }
        }
    }

    public void OnHedgingFill(object? sender, Fill fill)
    {
        if (fill.InstrumentId != _hedgeInstrument.InstrumentId) return;

        // 이미 Submit 시점에 수량을 반영했으므로 여기서는 로그만 남김
        _logger.LogInformationWithCaller($"[WS Fill] Hedge filled: {fill.Quantity} @ {fill.Price}.");
    }

    private void OnOrderStatusChanged(object? sender, OrderStatusReport report)
    {
        // 종료 상태만 처리 (New, PartiallyFilled 등은 AlgoOrder가 알아서 처리)
        switch (report.Status)
        {
            case OrderStatus.Cancelled:
            case OrderStatus.Rejected:
            case OrderStatus.Filled:
                ClearActiveOrder(report);
                break;
        }
    }

    private void ClearActiveOrder(OrderStatusReport finalReport)
    {
        lock (_stateLock)
        {
            if (_activeHedgeOrder is null || _activeHedgeOrder.ClientOrderId != finalReport.ClientOrderId) return;

            _logger.LogInformationWithCaller($"({_activeHedgeOrder.Side}) Hedge Order {_activeHedgeOrder.ClientOrderId} ended ({finalReport.Status}).");

            // 1. [Rollback] 체결되지 못하고 남은 잔량(LeavesQty) 복구
            // (Submit 시점에 전체 수량을 뺐으므로, 체결 안 된 부분은 다시 더해줘야 함)
            if (finalReport.LeavesQuantity.ToTicks() > 0)
            {
                lock (_pendingValueLock)
                {
                    decimal sign = _activeHedgeOrder.Side == Side.Buy ? 1 : -1;
                    _netPendingHedgeQuantity += Quantity.FromDecimal(finalReport.LeavesQuantity.ToDecimal() * sign);

                    _logger.LogInformationWithCaller($"Restoring {finalReport.LeavesQuantity} to pending. New pending: {_netPendingHedgeQuantity}");
                }
            }

            // 2. 자원 정리 (이벤트 구독 해제)
            if (_activeHedgeOrder is IDisposable disposableOrder)
            {
                disposableOrder.Dispose();
            }

            _activeHedgeOrder = null;
        }

        // 3. 주문이 끝났으므로, 남은 물량이나 복구된 물량 처리를 위해 다시 체크
        _ = CheckAndStartHedgeAsync();
    }

    private Quantity CalculateQuantityFromValue(Instrument instrument, Price price, decimal valueToHedge)
    {
        if (valueToHedge == 0m) return Quantity.FromDecimal(0m);

        var unitValue = instrument.ValueInDenominationCurrency(price, Quantity.FromDecimal(1m));
        if (unitValue.Amount <= 0m) return Quantity.FromDecimal(0m);

        decimal rawQty = valueToHedge / unitValue.Amount;
        var minOrderSizeTicks = instrument.MinOrderSize.ToTicks();
        if (minOrderSizeTicks <= 0) return Quantity.FromDecimal(0m);

        return Quantity.FromDecimal(rawQty);
    }

    public void Dispose()
    {
        Deactivate();
        lock (_stateLock)
        {
            if (_activeHedgeOrder is IDisposable d) d.Dispose();
            _activeHedgeOrder = null;
        }
    }
}