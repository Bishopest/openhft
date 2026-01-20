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

public class ShadowQuoter : IQuoter
{
    private readonly ILogger _logger;
    private readonly Side _side;
    private readonly Instrument _instrument;
    private readonly IOrderFactory _orderFactory;
    private readonly IMarketDataManager _marketDataManager;
    private readonly string _bookName;
    private readonly object _stateLock = new();

    private IOrder? _activeOrder;
    private bool _isOrderInFlight = false;

    public Quote? LatestQuote { get; private set; }
    public event Action? OrderFullyFilled;
    public event Action<Fill>? OrderFilled;

    public ShadowQuoter(ILogger logger, Side side, Instrument instrument, IOrderFactory orderFactory, string bookName, IMarketDataManager marketDataManager)
    {
        _logger = logger;
        _side = side;
        _instrument = instrument;
        _orderFactory = orderFactory;
        _bookName = bookName;
        _marketDataManager = marketDataManager;
    }

    public void UpdateParameters(QuotingParameters parameters) { /* Shadow는 별도 HittingLogic 미사용 (자체 로직) */ }

    public async Task UpdateQuoteAsync(Quote newQuote, bool isPostOnly, Quantity? availablePosition, CancellationToken cancellationToken = default)
    {
        LatestQuote = newQuote;

        // 1. 이미 진행 중인 주문이 있다면 중복 발주 방지
        lock (_stateLock)
        {
            if (_isOrderInFlight || _activeOrder != null) return;
        }

        // 2. 상대 호가 확인 (Marketable 여부 체크)
        var book = _marketDataManager.GetOrderBook(_instrument.InstrumentId);
        if (book == null) return;

        bool isMarketable = false;
        if (_side == Side.Buy)
        {
            var (bestAsk, _) = book.GetBestAsk();
            if (bestAsk.ToTicks() > 0 && newQuote.Price >= bestAsk) isMarketable = true;
        }
        else
        {
            var (bestBid, _) = book.GetBestBid();
            if (bestBid.ToTicks() > 0 && newQuote.Price <= bestBid) isMarketable = true;
        }

        // 3. 바로 체결 가능한 가격일 때만 주문 전송
        if (isMarketable)
        {
            await ExecuteShadowOrderAsync(newQuote, availablePosition, cancellationToken);
        }
    }

    private async Task ExecuteShadowOrderAsync(Quote quote, Quantity? availablePosition, CancellationToken cancellationToken)
    {
        var finalSize = quote.Size;

        if (_side == Side.Sell && _instrument.ProductType == ProductType.Spot)
        {
            var availableSize = availablePosition ?? Quantity.Zero;

            // If the available balance is smaller than our target size, use the balance.
            if (availableSize < finalSize)
            {
                _logger.LogInformationWithCaller($"({_side}) ShadowQuoter limiting Spot Sell size from {finalSize} to available {availableSize}.");
                finalSize = availableSize;
            }

            // If the final size is too small, abort the execution.
            if (finalSize.ToDecimal() < _instrument.MinOrderSize.ToDecimal())
            {
                _logger.LogWarningWithCaller($"({_side}) ShadowQuoter adjusted size {finalSize} is below min order size. Aborting execution.");
                return;
            }
        }

        var finalQuote = new Quote(quote.Price, finalSize);

        var orderBuilder = new OrderBuilder(_orderFactory, _instrument.InstrumentId, _side, _bookName, OrderSource.NonManual);

        var newOrder = orderBuilder
            .WithPrice(finalQuote.Price)
            .WithQuantity(finalQuote.Size)
            .WithOrderType(OrderType.Limit)
            .WithPostOnly(false) // Shadow는 Taker이므로 반드시 false
            .WithStatusChangedHandler(OnOrderStatusChanged)
            .WithFillHandler(OnOrderFilledInternal)
            .Build();

        lock (_stateLock)
        {
            _isOrderInFlight = true;
            _activeOrder = newOrder;
        }

        _logger.LogInformationWithCaller($"({_side}) Shadow hitting at {quote.Price}. CID: {newOrder.ClientOrderId}");
        await newOrder.SubmitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async void OnOrderStatusChanged(object? sender, OrderStatusReport report)
    {
        // 핵심 로직: 수동 IOC 구현
        // 주문이 거래소에 수락(New)되거나 부분 체결(PartiallyFilled)된 즉시 나머지를 취소합니다.
        if (report.Status == OrderStatus.New || report.Status == OrderStatus.PartiallyFilled)
        {
            if (report.LeavesQuantity.ToTicks() > 0)
            {
                _logger.LogInformationWithCaller($"({_side}) Shadow IOC: Canceling remaining {report.LeavesQuantity}.");
                await ((IOrder)sender!).CancelAsync();
            }
        }

        // 종료 상태 처리
        if (report.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected)
        {
            lock (_stateLock)
            {
                if (_activeOrder?.ClientOrderId == report.ClientOrderId)
                {
                    _activeOrder = null;
                    _isOrderInFlight = false;

                    if (report.Status == OrderStatus.Filled) OrderFullyFilled?.Invoke();
                }
            }
        }
    }

    private void OnOrderFilledInternal(object? sender, Fill fill) => OrderFilled?.Invoke(fill);

    public async Task CancelQuoteAsync(CancellationToken cancellationToken = default)
    {
        LatestQuote = null;
        IOrder? toCancel;
        lock (_stateLock) { toCancel = _activeOrder; }
        if (toCancel != null) await toCancel.CancelAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_activeOrder != null)
            {
                _activeOrder.RemoveStatusChangedHandler(OnOrderStatusChanged);
                _activeOrder.RemoveFillHandler(OnOrderFilledInternal);
            }
        }
    }
}