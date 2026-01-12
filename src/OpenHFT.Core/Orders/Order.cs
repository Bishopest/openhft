using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Core.Orders;

// A sample concrete Order class to make the builder work.
// Note how properties are mutable (public set) for the builder to use.
public class Order : IOrder, IOrderUpdatable
{
    private readonly ILogger _logger;
    private readonly IOrderRouter _router;
    private readonly IOrderGateway _gateway;
    public long ClientOrderId { get; }
    public string? ExchangeOrderId { get; internal set; }
    public OrderStatus Status { get; internal set; }
    public Side Side { get; }
    public int InstrumentId { get; }
    public string BookName { get; internal set; }


    // Mutable properties for the builder
    public Price Price { get; set; }
    public Quantity Quantity { get; set; }
    public Quantity LeavesQuantity { get; set; }
    public OrderType OrderType { get; set; } // Added for completeness
    public bool IsPostOnly { get; set; }

    public long LastUpdateTime { get; internal set; }
    public OrderStatusReport? LatestReport { get; internal set; }

    private readonly List<Fill> _fills = new List<Fill>();
    private readonly object _stateLock = new();

    /// <summary>
    /// A read-only list of all executions for this order.
    /// </summary>
    public IReadOnlyList<Fill> Fills
    {
        get
        {
            lock (_stateLock)
            {
                return _fills.ToList().AsReadOnly();
            }
        }
    }

    public virtual AlgoOrderType AlgoOrderType => AlgoOrderType.None;
    public event EventHandler<OrderStatusReport>? StatusChanged;
    public event EventHandler<Fill>? OrderFilled;

    /// <summary>
    /// Initializes a new instance of the <see cref="Order"/> class.
    /// Public for testing and direct instantiation, but in production, creation via IOrderFactory is recommended.
    /// </summary>
    public Order(long clientOrderId, int instrumentId, Side side, string bookName, IOrderRouter router, IOrderGateway gateway, ILogger<Order> logger)
    {
        ClientOrderId = clientOrderId;
        InstrumentId = instrumentId;
        Side = side;
        BookName = bookName;
        Status = OrderStatus.Pending;

        _router = router;
        _gateway = gateway;
        _logger = logger;
        _router.RegisterOrder(this);
    }

    public void AddStatusChangedHandler(EventHandler<OrderStatusReport> handler)
    {
        StatusChanged += handler;
    }

    public void RemoveStatusChangedHandler(EventHandler<OrderStatusReport> handler)
    {
        // Essential: Allow consumers to unsubscribe explicitly
        StatusChanged -= handler;
    }

    public void AddFillHandler(EventHandler<Fill> handler)
    {
        OrderFilled += handler;
    }

    public void RemoveFillHandler(EventHandler<Fill> handler)
    {
        OrderFilled -= handler;
    }

    // --- Action Methods ---
    public virtual async Task SubmitAsync(CancellationToken cancellationToken = default)
    {
        // Update internal state first to prevent race conditions
        Status = OrderStatus.NewRequest;

        var request = new NewOrderRequest(InstrumentId, ClientOrderId, Side, Price, Quantity, OrderType, IsPostOnly);
        var result = await _gateway.SendNewOrderAsync(request, cancellationToken);

        if (!result.IsSuccess)
        {
            // If the request fails immediately, create a rejection report.
            var failureReport = new OrderStatusReport(
                ClientOrderId, null, null, InstrumentId, Side, OrderStatus.Rejected, Price, Quantity, Quantity,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), result.FailureReason);
            OnStatusReportReceived(failureReport);
        }
        else if (result.InitialReport.HasValue)
        {
            // If the gateway returns an immediate report, process it.
            OnStatusReportReceived(result.InitialReport.Value);
        }
        // If successful but no immediate report, we wait for the WebSocket stream to provide updates.
    }

    /// <summary>
    /// Submits a request to replace the active order with a new price.
    /// </summary>
    public async Task ReplaceAsync(Price newPrice, OrderType orderType, CancellationToken cancellationToken = default)
    {
        // 1. Check if the order is in a state that can be replaced.
        if (Status != OrderStatus.New && Status != OrderStatus.PartiallyFilled)
        {
            // Or log a warning and return.
            _logger.LogWarningWithCaller($"Cannot replace order in '{Status}' state. Info => {ToString()}");
            return;
        }

        if (string.IsNullOrEmpty(ExchangeOrderId))
        {
            // This can happen if the 'New' confirmation from the exchange hasn't arrived yet.
            _logger.LogWarningWithCaller($"Cannot replace order: ExchangeOrderId is not yet known. Info => {ToString()}");
            return;
        }

        // 2. Update internal state to 'ReplaceRequest'
        var origStatus = Status;
        Status = OrderStatus.ReplaceRequest;

        // 3. Create request and call the gateway
        var request = new ReplaceOrderRequest(ExchangeOrderId, newPrice, InstrumentId);
        var result = await _gateway.SendReplaceOrderAsync(request, cancellationToken);

        // 4. Handle immediate failure
        if (!result.IsSuccess)
        {
            lock (_stateLock)
            {
                Status = origStatus;
            }
        }
        else if (result.Report.HasValue)
        {
            // If the gateway returns an immediate report, process it.
            OnStatusReportReceived(result.Report.Value);
        }
        // On success, we wait for a WebSocket update to confirm the replacement.
    }

    /// <summary>
    /// Submits a request to cancel the active order.
    /// </summary>
    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        // 1. Check if the order is in a cancellable state.
        if (Status is OrderStatus.Cancelled or OrderStatus.Filled or OrderStatus.Rejected or OrderStatus.CancelRequest)
        {
            // Already terminal or cancellation is in-flight.
            _logger.LogWarningWithCaller($"Cannot cancel order in '{Status}' state. Info => {ToString()}");
            return;
        }
        if (string.IsNullOrEmpty(ExchangeOrderId))
        {
            _logger.LogWarningWithCaller($"Cannot cancel order: ExchangeOrderId is not yet known. Info => {ToString()}");
            return;
        }

        // 2. Update internal state to 'CancelRequest'
        var origStatus = Status;
        Status = OrderStatus.CancelRequest;

        // 3. Create request and call the gateway
        var request = new CancelOrderRequest(ExchangeOrderId, InstrumentId);
        var result = await _gateway.SendCancelOrderAsync(request, cancellationToken);

        // 4. Handle immediate failure
        if (!result.IsSuccess)
        {
            lock (_stateLock)
            {
                Status = origStatus;
            }
        }
        else if (result.Report.HasValue)
        {
            // If the gateway returns an immediate report, process it.
            OnStatusReportReceived(result.Report.Value);
        }
        // On success, we wait for a WebSocket update to confirm the cancellation.
    }

    /// <summary>
    /// Synchronously marks the order for cancellation.
    /// </summary>
    public bool MarkAsCancelRequested()
    {
        lock (_stateLock)
        {
            // Only transition to CancelRequest if the order is in a live, cancellable state.
            if (Status is OrderStatus.New or OrderStatus.PartiallyFilled or OrderStatus.NewRequest or OrderStatus.ReplaceRequest)
            {
                Status = OrderStatus.CancelRequest;
                _logger.LogDebug("Order {ClientOrderId} has been marked for cancellation.", ClientOrderId);
                return true;
            }
        }
        _logger.LogWarningWithCaller($"Attempted to mark order {ClientOrderId} for cancellation, but its status is '{Status}'.");
        return false;
    }

    /// <summary>
    /// Reverts a transient status like 'CancelRequest' back to the last stable status.
    /// </summary>
    public void RevertPendingStateChange()
    {
        lock (_stateLock)
        {
            // Only revert if we are in a transient 'request' state.
            if (Status is OrderStatus.CancelRequest or OrderStatus.ReplaceRequest)
            {
                // Revert to the status from the latest report.
                // If there's no report yet (e.g., a New order that failed to submit),
                // revert to the initial Pending state.
                var previousStatus = LatestReport?.Status ?? OrderStatus.New;

                _logger.LogInformationWithCaller($"Reverting status for Order {ClientOrderId} from '{Status}' to '{previousStatus}' due to a failed API call.");
                Status = previousStatus;
            }
        }
    }

    public void OnStatusReportReceived(in OrderStatusReport report)
    {
        lock (_stateLock)
        {
            if (report.LastQuantity.HasValue &&
                report.LastPrice.HasValue &&
                report.ExecutionId != null &&
                report.LastQuantity.Value.ToDecimal() > 0m &&
                report.LastPrice.Value.ToDecimal() > 0m)
            {
                var fill = new Fill(
                    instrumentId: InstrumentId,
                    bookName: BookName,
                    clientOrderId: ClientOrderId,
                    exchangeOrderId: report.ExchangeOrderId ?? ExchangeOrderId ?? string.Empty,
                    executionId: report.ExecutionId,
                    side: Side,
                    price: report.LastPrice.Value,
                    quantity: report.LastQuantity.Value,
                    timestamp: report.Timestamp
                );

                if (!_fills.Any(f => f.ExecutionId == fill.ExecutionId))
                {
                    _fills.Add(fill);
                    OrderFilled?.Invoke(this, fill);
                    _router.RaiseOrderFilled(this, fill);
                }

                if (report.Status == OrderStatus.Filled)
                {
                    _router.DeregisterOrder(this);
                }
            }

            if (report.Timestamp < this.LastUpdateTime) return;

            Status = report.Status;
            Price = report.Price;
            Quantity = report.Quantity;
            LeavesQuantity = report.LeavesQuantity;
            LastUpdateTime = report.Timestamp;
            LatestReport = report;
            if (!string.IsNullOrEmpty(report.ExchangeOrderId))
            {
                ExchangeOrderId = report.ExchangeOrderId;
            }


            StatusChanged?.Invoke(this, report);
            _router.RaiseStatusChanged(this, report);

            // _logger.LogInformationWithCaller($"[Changed] {ToString()}");

            switch (report.Status)
            {
                case OrderStatus.Cancelled:
                case OrderStatus.Rejected:
                    _router.DeregisterOrder(this);
                    break;
            }
        }
    }

    /// <summary>
    /// Resets the order object to a clean state for reuse in an object pool.
    /// This is critical for GC-Free architecture to prevent old listeners from receiving new events.
    /// </summary>
    public void ResetState()
    {
        lock (_stateLock)
        {
            ExchangeOrderId = null;
            Status = OrderStatus.Pending;
            BookName = string.Empty;
            Price = default;
            Quantity = default;
            LeavesQuantity = default;
            LatestReport = null;
            _fills.Clear();

            // 2. Clear Event Subscribers (Aggressive cleanup)
            // This prevents "Ghost Events" where an old strategy keeps listening to a pooled order.
            StatusChanged = null;
            OrderFilled = null;
        }
    }

    /// <summary>
    /// Provides a concise, human-readable string representation of the order's current state.
    /// </summary>
    /// <returns>A string summarizing the order.</returns>
    public override string ToString()
    {
        // Example output:
        // [CID: 12345] BUY 1.5 BTC/USDT @ 50000.50 [New] (Leaves: 1.5) EXO: xyz-789
        // [CID: 12346] SELL 0.5 ETH/USDT @ 3000.25 [Filled] (Leaves: 0)
        string exoIdPart = string.IsNullOrEmpty(ExchangeOrderId) ? "" : $" EXO: {ExchangeOrderId}";

        return $"[CID: {ClientOrderId}] {Side.ToString().ToUpper()} {Quantity.ToDecimal()} " +
               $"(ID:{InstrumentId}) @ {Price.ToDecimal()} [{Status}] " +
               $"(Leaves: {LeavesQuantity.ToDecimal()}){exoIdPart}";
    }

}