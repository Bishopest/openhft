using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;
using OpenHFT.Feed;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

/// <summary>
/// Manages a cluster of orders corresponding to a single logical quote price within a grouping interval.
/// Handles layering (depth), hitting logic, and rate-limited updates.
/// </summary>
public class OrdersOnGroup
{
    private readonly ILogger _logger;
    private readonly Instrument _instrument;
    private readonly Side _side;
    private readonly IOrderFactory _orderFactory;
    private readonly IOrderGateway _orderGateway;
    private readonly string _bookName;
    private readonly IMarketDataManager _marketDataManager; // For hitting logic checks

    // Configuration
    private readonly int _depth;
    private readonly decimal _groupingBp;

    // State
    private readonly List<IOrder> _activeOrders = new();
    private readonly object _lock = new();
    // Create a semaphore with an initial and max count of 1.
    // This ensures that only one thread can enter the UpdateAsync logic at a time.
    private readonly SemaphoreSlim _updateSemaphore = new(1, 1);

    // 현재 이 그룹이 목표로 하는 기준 가격 (Engine에서 Grouping된 가격)
    public Price TargetQuotePrice { get; private set; }

    private bool _isCancellingForMove = false;

    // Events
    public event Action? OrderFullyFilled;
    public event Action<Fill>? OrderFilled;

    public OrdersOnGroup(
        ILogger logger,
        Instrument instrument,
        Side side,
        IOrderFactory orderFactory,
        IOrderGateway orderGateway,
        string bookName,
        IMarketDataManager marketDataManager,
        int depth,
        decimal groupingBp)
    {
        _logger = logger;
        _instrument = instrument;
        _side = side;
        _orderFactory = orderFactory;
        _orderGateway = orderGateway;
        _bookName = bookName;
        _marketDataManager = marketDataManager;
        _depth = Math.Max(1, depth);
        _groupingBp = groupingBp;
    }

    /// <summary>
    /// Updates the orders in this group to match the new target quote price.
    /// Performs only one API action (Create/Replace/Cancel) to respect rate limits,
    /// unless a full reset is required.
    /// </summary>
    public async Task UpdateAsync(Quote targetQuote, HittingLogic hittingLogic, bool isPostOnly, CancellationToken cancellationToken)
    {
        // Attempt to acquire the semaphore. If another update is in progress,
        // this will wait until it's released.
        await _updateSemaphore.WaitAsync(cancellationToken);

        try
        {
            // The entire reconciliation logic is now protected from concurrent execution.

            // 0. Check target quote price change
            if (TargetQuotePrice != targetQuote.Price)
            {
                _isCancellingForMove = true;
                TargetQuotePrice = targetQuote.Price;
            }
            else
            {
                if (IsFilling())
                {
                    return; // No change and filling, so exit early.
                }
            }

            // 1. Handle moving to a new price group
            if (_isCancellingForMove)
            {
                await CancelAllAsync(cancellationToken);

                lock (_lock)
                {
                    if (!_activeOrders.Any())
                    {
                        _isCancellingForMove = false;
                        // All orders are cancelled, we can now proceed to place new ones in the same call.
                    }
                    else
                    {
                        // Still have orders to cancel, so we stop here for this update cycle.
                        return;
                    }
                }
            }

            // 2. Normal order reconciliation process
            var layerPrices = CalculateLayerPrices(targetQuote.Price);
            var validPrices = ApplyHittingLogic(layerPrices, hittingLogic);

            // This method will now execute without interference.
            await ReconcileOrdersAsync(validPrices, targetQuote.Size, isPostOnly, cancellationToken);
        }
        finally
        {
            // CRITICAL: Always release the semaphore, even if an exception occurs.
            _updateSemaphore.Release();
        }
    }

    private bool IsFilling()
    {
        lock (_lock)
        {
            return _activeOrders.Any(o => o.LeavesQuantity < o.Quantity);
        }
    }

    private async Task CancelOneOrderAsync(CancellationToken token)
    {
        IOrder? orderToCancel = null;
        lock (_lock)
        {
            if (_side == Side.Buy)
            {
                // Buy: 가격이 높은 것이 안쪽 (Descending)
                orderToCancel = _activeOrders.OrderByDescending(o => o.Price.ToDecimal()).FirstOrDefault();
            }
            else
            {
                // Sell: 가격이 낮은 것이 안쪽 (Ascending)
                orderToCancel = _activeOrders.OrderBy(o => o.Price.ToDecimal()).FirstOrDefault();
            }
        }

        if (orderToCancel != null)
        {
            try
            {
                await orderToCancel.CancelAsync(token);
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCaller(ex, $"Failed to cancel order {orderToCancel.ClientOrderId}");
            }
        }
    }

    /// <summary>
    /// Cancels all active orders in this group.
    /// </summary>
    public async Task CancelAllAsync(CancellationToken cancellationToken)
    {
        List<IOrder> ordersToCancel;
        List<string> exchangeOrderIdsToCancel;

        // --- Phase 1: Identify and Mark orders for cancellation ---
        lock (_lock)
        {
            ordersToCancel = _activeOrders
                .Where(o => o.Status is OrderStatus.New or OrderStatus.PartiallyFilled) // Only target cancellable orders
                .ToList();

            if (!ordersToCancel.Any())
            {
                return;
            }

            foreach (var order in ordersToCancel)
            {
                order.MarkAsCancelRequested();
            }

            // Now, collect the IDs for the API call.
            exchangeOrderIdsToCancel = ordersToCancel
                .Where(o => !string.IsNullOrEmpty(o.ExchangeOrderId))
                .Select(o => o.ExchangeOrderId!)
                .ToList();
        }

        // If there are no orders with an ExchangeOrderId yet, there's nothing to send to the API.
        if (!exchangeOrderIdsToCancel.Any())
        {
            _logger.LogInformationWithCaller($"Identified {ordersToCancel.Count} orders to cancel, but none have an ExchangeOrderId yet. They will be handled by their state.");
            return;
        }

        // --- Phase 2: Send the API request and process the response ---
        _logger.LogDebug($"Attempting to bulk cancel {exchangeOrderIdsToCancel.Count} orders.");

        try
        {
            var request = new BulkCancelOrdersRequest(exchangeOrderIdsToCancel, _instrument.InstrumentId);
            var results = await _orderGateway.SendBulkCancelOrdersAsync(request, cancellationToken);

            // Process the response reports.
            // This part remains the same as your implementation, routing reports back.
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
                    _logger.LogWarningWithCaller($"Failed to cancel order {result.OrderId} during bulk op: {result.FailureReason}");

                    // Optional: Create a manual rejection report and route it.
                    var failedOrder = ordersToCancel.FirstOrDefault(o => o.ExchangeOrderId == result.OrderId);
                    failedOrder.RevertPendingStateChange();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "An exception occurred during the bulk cancellation API call.");
            lock (_lock)
            {
                foreach (var order in ordersToCancel)
                {
                    order.RevertPendingStateChange();
                }
            }
        }
    }

    private List<Price> CalculateLayerPrices(Price groupStartPrice)
    {
        var prices = new List<Price>();

        // Calculate spacing: Grouping Range / Depth
        // GroupingBp가 100bp이고 Depth가 3이면, 간격은 33.3bp
        // StartPrice부터 바깥쪽으로 퍼져나감.

        decimal startPriceDec = groupStartPrice.ToDecimal();
        decimal groupingRangeDec = startPriceDec * (_groupingBp * 0.0001m);
        decimal stepDec = groupingRangeDec / _depth;

        // Tick Size 보정
        var tickSize = _instrument.TickSize.ToDecimal();
        stepDec = Math.Max(tickSize, Math.Round(stepDec / tickSize) * tickSize);

        for (int i = 0; i < _depth; i++)
        {
            decimal priceDec;
            if (_side == Side.Buy)
            {
                priceDec = startPriceDec - (stepDec * i);
            }
            else
            {
                priceDec = startPriceDec + (stepDec * i);
            }

            if (priceDec > 0)
                prices.Add(Price.FromDecimal(priceDec));
        }

        return prices;
    }

    private List<Price> ApplyHittingLogic(List<Price> intendedPrices, HittingLogic logic)
    {
        if (logic == HittingLogic.AllowAll) return intendedPrices;

        var book = _marketDataManager.GetOrderBook(_instrument.InstrumentId);
        if (book == null) return intendedPrices; // Data not available, assume safe or default

        var bestBid = book.GetBestBid().price.ToDecimal();
        var bestAsk = book.GetBestAsk().price.ToDecimal();
        var tickSize = _instrument.TickSize.ToDecimal();

        var validPrices = new List<Price>();

        foreach (var p in intendedPrices)
        {
            decimal priceDec = p.ToDecimal();
            decimal adjustedPrice = priceDec;

            if (_side == Side.Buy)
            {
                if (priceDec >= bestBid) // Crossing spread (Taker)
                {
                    if (logic == HittingLogic.OurBest)
                    {
                        // Prevent crossing: Cap at Best Bid
                        // But if we already have orders at BestBid, do we stack? Assume yes for now.
                        adjustedPrice = bestBid;
                    }
                    else if (logic == HittingLogic.Pennying)
                    {
                        // Pennying: Best Bid + 1 Tick (but not crossing Best Ask)
                        adjustedPrice = Math.Min(bestBid + tickSize, bestAsk - tickSize);
                    }
                }
            }
            else // Sell
            {
                if (priceDec <= bestAsk) // Crossing spread (Taker)
                {
                    if (logic == HittingLogic.OurBest)
                    {
                        adjustedPrice = bestAsk;
                    }
                    else if (logic == HittingLogic.Pennying)
                    {
                        adjustedPrice = Math.Max(bestAsk - tickSize, bestBid + tickSize);
                    }
                }
            }

            validPrices.Add(Price.FromDecimal(adjustedPrice));
        }

        // 중복 제거 및 정렬 (Buy: Descending, Sell: Ascending -> 안쪽부터 바깥쪽 순서)
        var distinctPrices = validPrices.Distinct();
        return _side == Side.Buy
            ? distinctPrices.OrderByDescending(p => p.ToDecimal()).ToList()
            : distinctPrices.OrderBy(p => p.ToDecimal()).ToList();
    }

    private async Task ReconcileOrdersAsync(List<Price> targetPrices, Quantity sizePerOrder, bool isPostOnly, CancellationToken token)
    {
        // 1. 현재 활성 주문들을 '안쪽 -> 바깥쪽' 순서로 정렬하여 스냅샷 생성
        List<IOrder> sortedActiveOrders;
        lock (_lock)
        {
            if (_side == Side.Buy)
            {
                // Buy: 가격 높음(Market 근처) -> 낮음
                sortedActiveOrders = _activeOrders.OrderByDescending(o => o.Price.ToDecimal()).ToList();
            }
            else
            {
                // Sell: 가격 낮음(Market 근처) -> 높음
                sortedActiveOrders = _activeOrders.OrderBy(o => o.Price.ToDecimal()).ToList();
            }
        }

        // 2. 주문 개수 초과분 취소 (가장 안쪽부터)
        // 예: Target이 2개인데 Active가 3개면, 가장 안쪽(Index 0) 하나 취소
        // Hitting Logic으로 인해 안쪽 주문을 못 내게 된 상황 등
        int excessCount = sortedActiveOrders.Count - targetPrices.Count;
        if (excessCount > 0)
        {
            // 정렬된 리스트의 앞부분(안쪽)이 초과분이므로 취소
            var orderToCancel = sortedActiveOrders[0];
            // (또는 상황에 따라 HittingLogic이 막아서 안쪽을 비워야 한다면 0번 취소가 맞음)

            try
            {
                await orderToCancel.CancelAsync(token);
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCaller(ex, "Cancel failed in reconcile.");
            }
            return; // 1 Action Done
        }

        // 3. 정정 및 생성 (바깥쪽부터 안쪽으로)
        // targetPrices도 안쪽 -> 바깥쪽 순서로 들어옴 (ApplyHittingLogic 보장)
        // 따라서 Loop를 역순으로 돌면 바깥쪽부터 처리됨.

        // 매핑: targetPrices[i] <-> sortedActiveOrders[i - offset]
        // Active가 부족할 수 있으므로, 인덱스 매칭에 주의.
        // 여기서는 '바깥쪽'끼리 매칭해야 함.

        // 예: Target [In, Mid, Out] (3개) vs Active [Mid, Out] (2개)
        // Target Index 2 (Out) <-> Active Index 1 (Out)
        // Target Index 1 (Mid) <-> Active Index 0 (Mid)
        // Target Index 0 (In)  <-> Active None -> Create

        // 즉, Active Index = i - (TargetCount - ActiveCount)
        int offset = targetPrices.Count - sortedActiveOrders.Count;

        for (int i = targetPrices.Count - 1; i >= 0; i--)
        {
            var targetPrice = targetPrices[i];
            int activeIdx = i - offset;

            if (activeIdx >= 0)
            {
                // 대응되는 기존 주문 있음 -> 가격 비교 후 정정
                var existingOrder = sortedActiveOrders[activeIdx];

                if (existingOrder.Price != targetPrice)
                {
                    try
                    {
                        await existingOrder.ReplaceAsync(targetPrice, OrderType.Limit, token);
                        return; // 1 Action Done
                    }
                    catch (Exception ex)
                    {
                        _logger.LogErrorWithCaller(ex, "Replace failed in reconcile.");
                    }
                }
            }
            else
            {
                // 대응되는 주문 없음 -> 신규 생성 (안쪽 채우기)
                // 바깥쪽부터 Loop를 돌지만, offset 때문에 안쪽(Index < offset) 부분이 생성 대상으로 걸림

                await PlaceNewOrderAsync(targetPrice, sizePerOrder, isPostOnly, token);
                return; // 1 Action Done
            }
        }
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

        lock (_lock)
        {
            _activeOrders.Add(order);
        }

        await order.SubmitAsync(token);
    }

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
        lock (_lock)
        {
            var order = _activeOrders.FirstOrDefault(order => order.ClientOrderId == finalReport.ClientOrderId);
            if (order == null) return;

            _logger.LogDebug($"({_side}) Order {finalReport.ClientOrderId} reached terminal state {finalReport.Status}. Clearing active order.");

            // Unsubscribe to prevent memory leaks
            order.RemoveStatusChangedHandler(OnOrderStatusChanged);
            order.RemoveFillHandler(OnOrderFilled);

            _activeOrders.Remove(order);

            if (!_activeOrders.Any() && finalReport.Status == OrderStatus.Filled)
            {
                OrderFullyFilled?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        // Cancel all orders synchronously or fire-and-forget?
        // Usually dispose implies cleanup.
        _ = CancelAllAsync(CancellationToken.None);
    }
}
