using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

public class TrendQuoter : IQuoter
{
    private readonly ILogger _logger;
    private readonly Side _side;
    private readonly Instrument _instrument;
    private readonly IOrderFactory _orderFactory; // To create IOrder objects
    private readonly IMarketDataManager _marketDataManager;
    private readonly string _bookName;
    private readonly object _stateLock = new();
    private IOrder? _activeOrder;
    // --- State Management ---
    private Price _triggerPrice; // The price level that triggers our action. Set by UpdateQuoteAsync.
    private Quantity _quoteSize; // The size to accumulate per trigger.
    // The thread-safe target quantity to be executed.
    private long _targetQuantityInTicks;
    private Quantity TargetQuantity => Quantity.FromTicks(Interlocked.Read(ref _targetQuantityInTicks));
    /// <summary>
    /// The most recent quote that was requested to be updated.
    /// Null if the last action was a cancellation.
    /// </summary>
    public Quote? LatestQuote { get; private set; }
    public event Action? OrderFullyFilled;
    public event Action<Fill>? OrderFilled;

    public TrendQuoter(
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

    public void UpdateParameters(QuotingParameters parameters)
    {
        return;
    }

    /// <summary>
    /// This method NO LONGER places orders directly. It just updates the internal trigger price.
    /// </summary>
    public Task UpdateQuoteAsync(Quote newQuote, bool isPostOnly, CancellationToken cancellationToken = default)
    {
        // This method is now very lightweight and synchronous.
        lock (_stateLock)
        {
            _triggerPrice = newQuote.Price;
            _quoteSize = newQuote.Size;
            _logger.LogDebug("({Side}) Trigger price updated to {Price} with size {Size}.", _side, _triggerPrice, _quoteSize);
            var ob = _marketDataManager.GetBestOrderBook(_instrument.InstrumentId);
            if (ob is not null)
            {
                OnBestOrderBookUpdated(null, ob);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// This is the primary trigger for order execution, called on every best bid/ask update.
    /// </summary>
    private void OnBestOrderBookUpdated(object? sender, BestOrderBook book)
    {
        Price currentTriggerPrice;
        Quantity currentQuoteSize;

        lock (_stateLock)
        {
            currentTriggerPrice = _triggerPrice;
            currentQuoteSize = _quoteSize;
        }

        if (currentTriggerPrice.ToTicks() == 0) return; // Not configured yet.

        bool signalFired = false;

        // Determine if the signal is fired.
        if (_side == Side.Buy) // If our strategy side is Buy...
        {
            // ...we are looking for the market's best BID to cross our trigger price.
            if (book.GetBestAsk().price <= currentTriggerPrice)
            {
                signalFired = true;
            }
        }
        else // _side == Side.Sell
        {
            // If our strategy side is Sell, we look for the market's best ASK to cross.
            if (book.GetBestBid().price >= currentTriggerPrice)
            {
                signalFired = true;
            }
        }

        if (signalFired)
        {
            // A signal was fired. Accumulate the target quantity.
            // Using Interlocked.Add for thread-safe accumulation.
            var originalTarget = Interlocked.CompareExchange(ref _targetQuantityInTicks, currentQuoteSize.ToTicks(), 0);

            if (originalTarget == 0)
            {
                _logger.LogInformationWithCaller($"({_side}) Signal Fired! Market price crossed trigger {currentTriggerPrice}. Setting target quantity to: {currentQuoteSize}");
            }
        }

        // Check if we need to execute an order.
        // Use a non-blocking fire-and-forget task to avoid blocking the market data thread.
        if (TargetQuantity.ToTicks() > 0)
        {
            _ = ExecuteOrderAsync(book);
        }
        else
        {
            if (_activeOrder is not null)
            {
                _ = _activeOrder.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteOrderAsync(BestOrderBook currentBook)
    {
        IOrder? newOrder = null;
        try
        {
            // Determine the order side (opposite to the trend side) and price.
            var orderSide = _side == Side.Buy ? Side.Sell : Side.Buy;
            var orderPrice = orderSide == Side.Buy ? currentBook.GetBestBid().price : currentBook.GetBestAsk().price;
            var orderQuantity = TargetQuantity;
            var orderQuote = new Quote(orderPrice, orderQuantity);
            LatestQuote = orderQuote;

            if (_activeOrder == null)
            {
                var orderBuilder = new OrderBuilder(_orderFactory, _instrument.InstrumentId, orderSide, _bookName);
                newOrder = orderBuilder
                    .WithPrice(orderQuote.Price)
                    .WithQuantity(orderQuote.Size)
                    .WithOrderType(OrderType.Limit) // Aggressive limit order
                    .WithPostOnly(false) // This MUST be false to be a taker
                    .WithStatusChangedHandler(OnOrderStatusChanged)
                    .WithFillHandler(OnOrderFilled)
                    .Build();

                lock (_stateLock)
                {
                    if (_activeOrder != null) return; // Another execution started concurrently.
                    _activeOrder = newOrder;
                }

                _logger.LogInformation($"({_side}) Executing trend order: {orderSide} {orderQuantity} @ {orderPrice}");
                await newOrder.SubmitAsync();
            }
            else
            {
                if (orderPrice != _activeOrder.Price)
                {
                    await _activeOrder.ReplaceAsync(orderPrice, OrderType.Limit, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"({_side}) Failed to execute trend order.");
        }
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
        // Thread-safely decrement the target quantity by the filled amount.
        var filledTicks = fill.Quantity.ToTicks();
        long currentTarget;
        long newTarget;
        do
        {
            currentTarget = Interlocked.Read(ref _targetQuantityInTicks);
            newTarget = Math.Max(0, currentTarget - filledTicks);
        }
        while (Interlocked.CompareExchange(ref _targetQuantityInTicks, newTarget, currentTarget) != currentTarget);

        _logger.LogInformationWithCaller($"({_side}) Trend order filled for {fill.Quantity}. Remaining target quantity: {Quantity.FromTicks(newTarget)}");

        // Forward the event.
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
