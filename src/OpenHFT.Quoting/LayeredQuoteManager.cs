using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

/// <summary>
/// Manages a series of layered orders for a single side based on a fixed price interval.
/// This class is designed to be controlled by a parent IQuoter implementation.
/// </summary>
public sealed class LayeredQuoteManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly Instrument _instrument;
    private readonly Side _side;
    private readonly IOrderFactory _orderFactory;
    private readonly IOrderGateway _orderGateway;
    private readonly string _bookName;

    // Configuration
    private readonly int _initialDepth;
    private readonly decimal _groupingBp;

    // State
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _updateSemaphore = new(1, 1);
    private readonly Dictionary<Price, IOrder> _activeOrders = new();
    private readonly List<Price> _sortedPrices = new(); // Sorted from inner to outer
    private Price? _priceInterval; // The fixed price step between layers

    // Events
    public event Action<Fill>? OrderFilled;

    public LayeredQuoteManager(
        ILogger logger,
        Side side,
        Instrument instrument,
        IOrderFactory orderFactory,
        IOrderGateway orderGateway,
        string bookName,
        int depth,
        decimal groupingBp)
    {
        _logger = logger;
        _side = side;
        _instrument = instrument;
        _orderFactory = orderFactory;
        _orderGateway = orderGateway;
        _bookName = bookName;
        _initialDepth = Math.Max(1, depth);
        _groupingBp = groupingBp;
    }

    /// <summary>
    /// Retrieves the active order at the innermost price level.
    /// The innermost order is the one closest to the market mid-price
    /// (highest price for Bids, lowest price for Asks).
    /// </summary>
    /// <returns>The IOrder instance for the innermost order, or null if no orders are active.</returns>
    public IOrder? GetMostInnerOrder()
    {
        lock (_stateLock)
        {
            if (!_sortedPrices.Any())
            {
                return null;
            }

            var innerPrice = _sortedPrices.First();

            if (_activeOrders.TryGetValue(innerPrice, out var innerOrder))
            {
                return innerOrder;
            }

            _logger.LogWarningWithCaller($"({_side}) Inconsistency found: Price {innerPrice} exists in sorted list but not in active order dictionary.");
            return null;
        }
    }

    /// <summary>
    /// The main entry point to update the quote layers.
    /// On first call, it initializes all layers. On subsequent calls, it adds one outer layer.
    /// </summary>
    public async Task UpdateAsync(Quote targetQuote, bool isPostOnly, CancellationToken cancellationToken)
    {
        await _updateSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_priceInterval == null)
            {
                InitializePriceInterval(targetQuote);
            }

            if (IsInnerOrderFilling(targetQuote))
            {
                _logger.LogDebug("({Side}) Inner order is filling, no action will be taken.", _side);
                return;
            }

            var workingDepth = _activeOrders.Count;

            if (workingDepth < _initialDepth)
            {
                await AppendAsync(targetQuote, isPostOnly, cancellationToken);
            }
            else
            {
                await TrimAsync(targetQuote, cancellationToken);
            }
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }

    public async Task AppendAsync(Quote quote, bool isPostOnly, CancellationToken cancellationToken)
    {
        if (_priceInterval is null)
        {
            return;
        }

        if (!_sortedPrices.Any())
        {
            await PlaceNewOrderAsync(quote.Price, quote.Size, isPostOnly, cancellationToken);
            return;
        }

        var mostInnerPriceDec = _sortedPrices.First().ToDecimal();
        var mostOuterPriceDec = _sortedPrices.Last().ToDecimal();
        var direction = _side == Side.Buy ? 1m : -1m;
        var signedQuotePriceDec = quote.Price.ToDecimal() * direction;
        var signedInterval = _priceInterval.Value.ToDecimal() * direction;

        if (signedQuotePriceDec >= (mostInnerPriceDec + signedInterval) * direction)
        {
            var submitPrice = mostInnerPriceDec + signedInterval;
            await PlaceNewOrderAsync(Price.FromDecimal(submitPrice), quote.Size, isPostOnly, cancellationToken);
        }
        else
        {
            var submitPrice = mostOuterPriceDec - signedInterval;
            await PlaceNewOrderAsync(Price.FromDecimal(submitPrice), quote.Size, isPostOnly, cancellationToken);
        }
    }

    public async Task TrimAsync(Quote quote, CancellationToken cancellationToken)
    {
        var workingDepth = _activeOrders.Count;
        if (workingDepth <= 0)
        {
            return;
        }

        // cancel surplus
        var mostInnerPrice = _sortedPrices.First();
        var mostOuterPrice = _sortedPrices.Last();
        IOrder? outerOrder;
        IOrder? innerOrder;
        lock (_stateLock)
        {
            if (!_sortedPrices.Any()) return;
            outerOrder = _activeOrders[_sortedPrices.Last()];
            innerOrder = _activeOrders[_sortedPrices.First()];
        }

        if (outerOrder is null || innerOrder is null)
        {
            _logger.LogWarningWithCaller($"Failed TrimAsync because of null inner/outer orders on {_instrument.Symbol}-{_side}");
            return;
        }

        if (workingDepth > _initialDepth)
        {
            await outerOrder.CancelAsync();
        }

        if (_priceInterval is null)
        {
            return;
        }

        // replace intto inner or outer price level
        var direction = _side == Side.Buy ? 1m : -1m;
        var signedQuotePriceDec = quote.Price.ToDecimal() * direction;
        var signedInterval = _priceInterval.Value.ToDecimal() * direction;

        if (signedQuotePriceDec >= (mostInnerPrice.ToDecimal() + signedInterval) * direction)
        {
            var isInnerPartiallyFilled = innerOrder.LeavesQuantity < innerOrder.Quantity;
            if (isInnerPartiallyFilled)
            {
                await innerOrder.CancelAsync(cancellationToken);
                return;
            }

            var newInnerPrice = Price.FromDecimal(mostInnerPrice.ToDecimal() + signedInterval);
            await ReplaceOrderAndUpdateStateAsync(outerOrder, newInnerPrice, cancellationToken);
        }
        else if (signedQuotePriceDec < mostInnerPrice.ToDecimal() * direction)
        {
            var newOuterPrice = Price.FromDecimal(mostOuterPrice.ToDecimal() - signedInterval);
            await ReplaceOrderAndUpdateStateAsync(innerOrder, newOuterPrice, cancellationToken);
        }
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken)
    {
        List<IOrder> ordersToCancel;
        List<string> exchangeOrderIdsToCancel;

        lock (_stateLock)
        {
            ordersToCancel = _activeOrders.Values.Where(o => o.Status is OrderStatus.New or OrderStatus.PartiallyFilled).ToList();
            if (!ordersToCancel.Any()) return;
            ordersToCancel.ForEach(o => o.MarkAsCancelRequested());
            exchangeOrderIdsToCancel = ordersToCancel.Where(o => !string.IsNullOrEmpty(o.ExchangeOrderId)).Select(o => o.ExchangeOrderId!).ToList();
        }

        if (!exchangeOrderIdsToCancel.Any()) return;

        try
        {
            var request = new BulkCancelOrdersRequest(exchangeOrderIdsToCancel, _instrument.InstrumentId);
            var results = await _orderGateway.SendBulkCancelOrdersAsync(request, cancellationToken);

            foreach (var result in results)
            {
                if (result.Report.HasValue)
                {
                    var orderToUpdate = ordersToCancel.FirstOrDefault(o => o.ExchangeOrderId == result.Report.Value.ExchangeOrderId);
                    if (orderToUpdate is Order concreteOrder)
                    {
                        concreteOrder.OnStatusReportReceived(result.Report.Value);
                    }
                }
                else if (!result.IsSuccess)
                {
                    var failedOrder = ordersToCancel.FirstOrDefault(o => o.ExchangeOrderId == result.OrderId);
                    failedOrder?.RevertPendingStateChange();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Bulk cancel API call failed. Reverting status for marked orders.");
            ordersToCancel.ForEach(o => o.RevertPendingStateChange());
        }
    }

    private void InitializePriceInterval(Quote initialQuote)
    {
        // --- MODIFICATION START ---
        // The price interval is now DIRECTLY derived from groupingBp.
        decimal startPriceDec = initialQuote.Price.ToDecimal();

        // 1. Calculate the fixed price step based on groupingBp.
        decimal stepDec = startPriceDec * (_groupingBp * 0.0001m);

        // 2. Align the step to the instrument's tick size.
        var tickSize = _instrument.TickSize.ToDecimal();
        if (tickSize > 0)
        {
            stepDec = Math.Max(tickSize, Math.Round(stepDec / tickSize) * tickSize);
        }

        if (stepDec <= 0)
        {
            _logger.LogWarning($"({_side}) Calculated price interval is zero or negative. Using a single layer.");
            _priceInterval = Price.FromDecimal(tickSize);
            return;
        }

        _priceInterval = Price.FromDecimal(stepDec);
        _logger.LogInformationWithCaller($"({_side}) Calculated price interval: {stepDec}");
    }

    private async Task PlaceNewOrderAsync(Price price, Quantity quantity, bool isPostOnly, CancellationToken token)
    {
        var orderBuilder = new OrderBuilder(_orderFactory, _instrument.InstrumentId, _side, _bookName, OrderSource.NonManual);
        var order = orderBuilder
            .WithPrice(price)
            .WithQuantity(quantity)
            .WithOrderType(OrderType.Limit)
            .WithPostOnly(isPostOnly)
            .WithStatusChangedHandler(OnOrderStatusChanged)
            .WithFillHandler(OnOrderFilled)
            .Build();

        lock (_stateLock)
        {
            if (_activeOrders.ContainsKey(price)) return; // Avoid duplicates
            _activeOrders[price] = order;
            _sortedPrices.Add(price);
            _sortedPrices.Sort(_side == Side.Buy ? (p1, p2) => p2.CompareTo(p1) : (p1, p2) => p1.CompareTo(p2));
        }

        await order.SubmitAsync(token);
    }

    /// <summary>
    /// A helper method that wraps the ReplaceAsync call and handles the synchronous
    /// state update of the internal collections (_activeOrders and _sortedPrices).
    /// </summary>
    private async Task ReplaceOrderAndUpdateStateAsync(IOrder orderToReplace, Price newPrice, CancellationToken cancellationToken)
    {
        // --- Phase 1: Pre-flight checks and state capture ---
        Price oldPrice;
        lock (_stateLock)
        {
            var entry = _activeOrders.FirstOrDefault(kvp => ReferenceEquals(kvp.Value, orderToReplace));
            if (entry.Value is null)
            {
                _logger.LogWarningWithCaller($"({_side}) Order {orderToReplace.ClientOrderId} to be replaced no longer exists in the active list. Aborting replace.");
                return;
            }
            oldPrice = entry.Key;

            if (_activeOrders.ContainsKey(newPrice))
            {
                _logger.LogWarningWithCaller($"({_side}) Cannot replace to price {newPrice} as an order already exists. Cancelling moving order {orderToReplace.ClientOrderId}.");
                _ = orderToReplace.CancelAsync(cancellationToken); // Fire-and-forget cancel
                return;
            }
        }

        // --- Phase 2: API Call ---
        try
        {
            await orderToReplace.ReplaceAsync(newPrice, OrderType.Limit, cancellationToken);

            // --- Phase 3: State Update (only on success) ---
            lock (_stateLock)
            {
                if (!_activeOrders.ContainsKey(oldPrice) || !ReferenceEquals(_activeOrders[oldPrice], orderToReplace))
                {
                    _logger.LogWarningWithCaller($"({_side}) Order {orderToReplace.ClientOrderId} was removed from active list during a replace operation. State update aborted.");
                    return;
                }

                // 1. Remove the old price entry.
                _activeOrders.Remove(oldPrice);
                _sortedPrices.Remove(oldPrice);

                // 2. Add the new price entry.
                // in case of not replaced order, clarify actual price of the order supposing that it received rest api response.
                var validNewPrice = orderToReplace.Price;
                _activeOrders[validNewPrice] = orderToReplace;
                _sortedPrices.Add(validNewPrice);

                // 3. Re-sort the price list.
                _sortedPrices.Sort(_side == Side.Buy ? (p1, p2) => p2.CompareTo(p1) : (p1, p2) => p1.CompareTo(p2));

                _logger.LogDebug("({Side}) Internal state updated for replace from {OldPrice} to {NewPrice}.",
                    _side, oldPrice, validNewPrice);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarningWithCaller($"({_side}) Replace request from {oldPrice} to {newPrice} was superseded by throttling gateway. No state change needed.");
            // The Order object's state will be reverted internally. Our collections were not changed, so we are consistent.
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"({_side}) API call to replace order from {oldPrice} to {newPrice} failed.");
            // The Order object's state will be reverted internally. Our collections were not changed, so we are consistent.
        }
    }

    private void OnOrderStatusChanged(object? sender, OrderStatusReport report)
    {
        if (report.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected)
        {
            ClearActiveOrder(report);
        }
    }

    private void OnOrderFilled(object? sender, Fill fill)
    {
        OrderFilled?.Invoke(fill);
    }

    private bool IsInnerOrderFilling(Quote quote)
    {
        lock (_stateLock)
        {
            if (!_sortedPrices.Any()) return false;
            var direction = _side == Side.Buy ? 1m : -1m;
            var signedQuotePriceDec = quote.Price.ToDecimal() * direction;
            var innerOrder = _activeOrders[_sortedPrices.First()];
            var onFilling = innerOrder.LeavesQuantity < innerOrder.Quantity;
            var shouldAggressive = innerOrder.Price.ToDecimal() * direction < signedQuotePriceDec;
            return onFilling && !shouldAggressive;
        }
    }

    private void ClearActiveOrder(OrderStatusReport finalReport)
    {
        lock (_stateLock)
        {
            var entryToRemove = _activeOrders
                .FirstOrDefault(kvp => kvp.Value.ClientOrderId == finalReport.ClientOrderId);

            // If the KeyValuePair's key is null (i.e., not found), it means the order is not in our dictionary.
            // This is equivalent to `default(KeyValuePair<...>)` for structs.
            if (entryToRemove.Value is null)
            {
                _logger.LogWarningWithCaller($"({_side}) Received a terminal report for Order {finalReport.ClientOrderId}, but it was not found in the active list. It might have been cleared already.");
                return;
            }

            Price keyPrice = entryToRemove.Key;
            IOrder orderObject = entryToRemove.Value;

            if (_activeOrders.Remove(keyPrice))
            {
                _sortedPrices.Remove(keyPrice);
                _logger.LogInformationWithCaller($"({_side}) Order {orderObject.ClientOrderId} at key-price {keyPrice} reached terminal state {finalReport.Status}. Clearing from manager.");
                orderObject.RemoveStatusChangedHandler(OnOrderStatusChanged);
                orderObject.RemoveFillHandler(OnOrderFilled);
            }
            else
            {
                // This case should be extremely rare, as we just found the key.
                // It could only happen if another thread removed the item between the Find and Remove calls,
                // but the lock prevents this. This log is for sanity checking.
                _logger.LogWarningWithCaller($"({_side}) CRITICAL ERROR: Found order {orderObject.ClientOrderId} with key {keyPrice} but failed to remove it from the dictionary. Concurrency issue suspected despite lock.");
            }
        }
    }

    public void Dispose()
    {
        _ = CancelAllAsync(CancellationToken.None);
        _updateSemaphore.Dispose();
    }
}