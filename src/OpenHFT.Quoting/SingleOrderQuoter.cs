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
    /// <summary>
    /// The most recent quote that was requested to be updated.
    /// Null if the last action was a cancellation.
    /// </summary>
    public Quote? LatestQuote { get; private set; }
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

    public async Task UpdateQuoteAsync(Quote newQuote, bool isPostOnly, CancellationToken cancellationToken = default)
    {
        var quoteP = newQuote.Price;

        try
        {
            LatestQuote = newQuote;
            IOrder? currentOrder;
            lock (_stateLock)
            {
                currentOrder = _activeOrder;
            }

            if (currentOrder is null)
            {
                // No active order, so place a new one.
                await StartNewQuoteAsync(newQuote, isPostOnly, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (newQuote.Price != currentOrder.Price)
                {
                    bool isPartiallyFilled = currentOrder.Quantity > currentOrder.LeavesQuantity;
                    var book = GetOrderBookFast();
                    bool isNearMid = false;
                    if (book is not null)
                    {
                        var mid = book.GetMidPrice();
                        var upperPrice = mid.ToDecimal() * (1m + 3m * 1e-4m);
                        var lowerPrice = mid.ToDecimal() * (1m - 3m * 1e-4m);
                        isNearMid = newQuote.Price.ToDecimal() >= lowerPrice && newQuote.Price.ToDecimal() <= upperPrice;
                    }

                    if (isPartiallyFilled && !isNearMid)
                    {
                        await currentOrder.CancelAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await currentOrder.ReplaceAsync(newQuote.Price, OrderType.Limit, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"({_side}) An unexpected error occurred during UpdateQuoteAsync.");
        }
    }

    public async Task CancelQuoteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            LatestQuote = null;
            IOrder? orderToCancel;
            lock (_stateLock)
            {
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
        var orderBuilder = new OrderBuilder(_orderFactory, _instrument.InstrumentId, _side, _bookName);
        var newOrder = orderBuilder
            .WithPrice(quote.Price)
            .WithQuantity(quote.Size)
            .WithOrderType(OrderType.Limit)
            .WithPostOnly(isPostOnly)
            .WithStatusChangedHandler(OnOrderStatusChanged)
            .Build();
        newOrder.OrderFilled += OnOrderFilled;

        lock (_stateLock)
        {
            // Ensure there isn't already an active order (race condition check)
            if (_activeOrder is not null)
            {
                _logger.LogWarningWithCaller($"({_side}) Aborting StartNewQuoteAsync; an active order was created concurrently.");
                newOrder.StatusChanged -= OnOrderStatusChanged;
                newOrder.OrderFilled -= OnOrderFilled;
                return;
            }

            _activeOrder = newOrder;
        }

        _logger.LogInformationWithCaller($"({_side}) Submitting new order {newOrder.ClientOrderId} for quote: {quote}");

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

            _logger.LogInformationWithCaller($"({_side}) Order {_activeOrder.ClientOrderId} reached terminal state {finalReport.Status}. Clearing active order.");

            // fully filled process
            if (finalReport.Status == OrderStatus.Filled)
            {
                _logger.LogInformationWithCaller($"Active order {finalReport.ClientOrderId} has been fully filled. Trigerring cooldown.");
                OrderFullyFilled?.Invoke();
            }

            // Unsubscribe to prevent memory leaks
            _activeOrder.StatusChanged -= OnOrderStatusChanged;
            _activeOrder.OrderFilled -= OnOrderFilled;
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
                _activeOrder.StatusChanged -= OnOrderStatusChanged;
                _activeOrder.OrderFilled -= OnOrderFilled;
                _activeOrder = null; // Or consider sending a final cancel request.
            }
        }
    }
}