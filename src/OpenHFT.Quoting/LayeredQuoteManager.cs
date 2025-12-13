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
        else
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
        var orderBuilder = new OrderBuilder(_orderFactory, _instrument.InstrumentId, _side, _bookName);
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
        Price oldPrice = orderToReplace.Price;

        // Check for price duplication before sending the request.
        lock (_stateLock)
        {
            if (_activeOrders.ContainsKey(newPrice))
            {
                _logger.LogWarningWithCaller($"({_side}) Cannot replace order to price {newPrice} because an order already exists at that level. Cancelling the moving order instead.");
                // If the target price is already occupied, the simplest recovery is to cancel the moving order.
                _ = orderToReplace.CancelAsync(cancellationToken);
                return;
            }
        }

        try
        {
            // The API call is made outside the lock.
            await orderToReplace.ReplaceAsync(newPrice, OrderType.Limit, cancellationToken);

            if (orderToReplace.Price != newPrice)
            {
                return;
            }

            // After the API call succeeds, update the internal state.
            lock (_stateLock)
            {
                // 1. Remove the old price entry from both collections.
                _activeOrders.Remove(oldPrice);
                _sortedPrices.Remove(oldPrice);

                // 2. Add the new price entry.
                _activeOrders[newPrice] = orderToReplace;
                _sortedPrices.Add(newPrice);

                // 3. Re-sort the price list to maintain the inner-to-outer order.
                _sortedPrices.Sort(_side == Side.Buy ? (p1, p2) => p2.CompareTo(p1) : (p1, p2) => p1.CompareTo(p2));

                _logger.LogDebug("({Side}) Successfully replaced order from {OldPrice} to {NewPrice}.",
                    _side, oldPrice, newPrice);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarningWithCaller($"({_side}) Replace order request from {oldPrice} to {newPrice} was superseded.");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"({_side}) Failed to replace order from {oldPrice} to {newPrice}. The order state might be inconsistent.");
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
            var order = _activeOrders.Values.FirstOrDefault(o => o.ClientOrderId == finalReport.ClientOrderId);
            if (order == null) return;

            if (_activeOrders.Remove(order.Price) && _sortedPrices.Remove(order.Price))
            {
                _logger.LogInformationWithCaller($"({_side}) Order {finalReport.ClientOrderId} at {order.Price} reached terminal state {finalReport.Status}. Clearing.");
                order.RemoveStatusChangedHandler(OnOrderStatusChanged);
                order.RemoveFillHandler(OnOrderFilled);
            }
        }
    }

    public void Dispose()
    {
        _ = CancelAllAsync(CancellationToken.None);
        _updateSemaphore.Dispose();
    }
}