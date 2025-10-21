using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

/// <summary>
/// Manages a single active order for one side (Bid or Ask) of an instrument's order book.
/// It handles placing, modifying, and cancelling the order based on incoming quotes.
/// This class is designed to be thread-safe for its public methods.
/// </summary>
public sealed class Quoter : IQuoter
{
    private readonly ILogger _logger;
    private readonly Side _side;
    private readonly int _instrumentId;
    private readonly IOrderFactory _orderFactory; // To create IOrder objects
    private readonly object _stateLock = new();

    private IOrder? _activeOrder;

    // 0 = idle, 1 = request in progress. Prevents concurrent modification attempts.
    private int _requestInProgressFlag;

    public Quoter(
        ILogger logger,
        Side side,
        int instrumentId,
        IOrderFactory orderFactory)
    {
        _logger = logger;
        _side = side;
        _instrumentId = instrumentId;
        _orderFactory = orderFactory;
    }

    public async Task UpdateQuoteAsync(Quote newQuote, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _requestInProgressFlag, 1, 0) != 0)
        {
            _logger.LogWarningWithCaller($"({_side}) UpdateQuoteAsync rejected: Another operation is already in progress.");
            return;
        }

        try
        {
            IOrder? currentOrder;
            lock (_stateLock)
            {
                currentOrder = _activeOrder;
            }

            if (currentOrder is null)
            {
                // No active order, so place a new one.
                await StartNewQuoteAsync(newQuote, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (currentOrder.Quantity > currentOrder.LeavesQuantity)
                {
                    await currentOrder.CancelAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await currentOrder.ReplaceAsync(newQuote.Price, OrderType.Limit, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"({_side}) An unexpected error occurred during UpdateQuoteAsync.");
        }
        finally
        {
            Interlocked.Exchange(ref _requestInProgressFlag, 0);
        }
    }

    public async Task CancelQuoteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _requestInProgressFlag, 1, 0) != 0)
        {
            _logger.LogWarningWithCaller($"({_side}) CancelQuoteAsync rejected: Another operation is already in progress.");
            return;
        }

        try
        {
            IOrder? orderToCancel;
            lock (_stateLock)
            {
                orderToCancel = _activeOrder;
            }

            if (orderToCancel is null)
            {
                _logger.LogInformationWithCaller($"({_side}) No active quote to cancel.");
                return;
            }

            await orderToCancel.CancelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"({_side}) An unexpected error occurred during CancelQuoteAsync.");
        }
        finally
        {
            Interlocked.Exchange(ref _requestInProgressFlag, 0);
        }
    }

    private async Task StartNewQuoteAsync(Quote quote, CancellationToken cancellationToken)
    {
        // Use the factory and builder to create a new order object.
        // The builder logic is now encapsulated elsewhere.
        var orderBuilder = new OrderBuilder(_orderFactory, _instrumentId, _side);
        var newOrder = orderBuilder
            .WithPrice(quote.Price)
            .WithQuantity(quote.Size)
            .WithOrderType(OrderType.Limit)
            .WithStatusChangedHandler(OnOrderStatusChanged)
            .Build();

        lock (_stateLock)
        {
            // Ensure there isn't already an active order (race condition check)
            if (_activeOrder is not null)
            {
                _logger.LogWarningWithCaller($"({_side}) Aborting StartNewQuoteAsync; an active order was created concurrently.");
                newOrder.StatusChanged -= OnOrderStatusChanged;
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

            // Unsubscribe to prevent memory leaks
            _activeOrder.StatusChanged -= OnOrderStatusChanged;
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
                _activeOrder = null; // Or consider sending a final cancel request.
            }
        }
    }


}