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

public class ShadowMakerQuoter : IQuoter
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

    public ShadowMakerQuoter(
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

    public void UpdateParameters(QuotingParameters parameters) { /* Shadow는 별도 HittingLogic 미사용 (자체 로직) */ }

    public async Task UpdateQuoteAsync(Quote newQuote, bool isPostOnly, CancellationToken cancellationToken = default)
    {
        LatestQuote = newQuote;

        IOrder? currentOrder;
        lock (_stateLock)
        {
            currentOrder = _activeOrder;
            if (_isOrderInFlight) return;
        }

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

        if (isMarketable)
        {
            if (currentOrder != null)
            {
                lock (_stateLock) { _isOrderInFlight = true; }
                await currentOrder.ReplaceAsync(newQuote.Price, OrderType.Limit, cancellationToken);
                _logger.LogInformationWithCaller($"({_side}) ShadowMaker reaplace hitting from {currentOrder.Price} to {newQuote.Price}. CID: {currentOrder.ClientOrderId}");
            }
            else
            {
                await ExecuteShadowOrderAsync(newQuote, cancellationToken);
            }
        }
        else
        {
            if (currentOrder != null)
            {
                var ob = _marketDataManager.GetOrderBook(_instrument.InstrumentId);
                if (ob != null)
                {
                    bool shouldCancel = false;
                    if (_side == Side.Buy)
                    {
                        var (bestBid, _) = ob.GetBestBid();
                        if (bestBid.ToTicks() > currentOrder.Price.ToTicks()) shouldCancel = true;
                    }
                    else
                    {
                        var (bestAsk, _) = ob.GetBestAsk();
                        if (bestAsk.ToTicks() > 0 && bestAsk.ToTicks() < currentOrder.Price.ToTicks()) shouldCancel = true;
                    }

                    if (shouldCancel)
                    {
                        _logger.LogInformationWithCaller($"({_side}) ShadowMaker: Outquoted. Canceling order {currentOrder.ClientOrderId}");
                        await currentOrder.CancelAsync(cancellationToken);
                    }
                }
            }
        }
    }

    private async Task ExecuteShadowOrderAsync(Quote quote, CancellationToken cancellationToken)
    {
        var orderBuilder = new OrderBuilder(_orderFactory, _instrument.InstrumentId, _side, _bookName, OrderSource.NonManual);

        var newOrder = orderBuilder
            .WithPrice(quote.Price)
            .WithQuantity(quote.Size)
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

        _logger.LogInformationWithCaller($"({_side}) ShadowMaker hitting at {quote.Price}. CID: {newOrder.ClientOrderId}");
        await newOrder.SubmitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async void OnOrderStatusChanged(object? sender, OrderStatusReport report)
    {
        if (report.Status == OrderStatus.New || report.Status == OrderStatus.PartiallyFilled)
        {
            lock (_stateLock)
            {
                _isOrderInFlight = false;
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