using System;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Orders;

// A sample concrete Order class to make the builder work.
// Note how properties are mutable (public set) for the builder to use.
public class Order : IOrder, IOrderUpdatable
{
    private readonly IOrderRouter _router;
    private readonly IOrderGateway _gateway;
    public long ClientOrderId { get; }
    public string? ExchangeOrderId { get; internal set; }
    public OrderStatus Status { get; internal set; }
    public Side Side { get; }
    public int InstrumentId { get; } // Added for completeness

    // Mutable properties for the builder
    public Price Price { get; internal set; }
    public Quantity Quantity { get; internal set; }
    public Quantity LeavesQuantity { get; internal set; }
    public OrderType OrderType { get; internal set; } // Added for completeness

    public long LastUpdateTime { get; internal set; }
    public OrderStatusReport? LatestReport { get; internal set; }

    public event EventHandler<OrderStatusReport>? StatusChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="Order"/> class.
    /// Public for testing and direct instantiation, but in production, creation via IOrderFactory is recommended.
    /// </summary>
    public Order(int instrumentId, Side side, IOrderRouter router, IOrderGateway gateway)
    {
        InstrumentId = instrumentId;
        Side = side;
        ClientOrderId = GenerateClientId(); // Should be a robust method
        Status = OrderStatus.Pending;

        _router = router;
        _gateway = gateway;

        _router.RegisterOrder(this);
    }

    // --- Action Methods ---
    public async Task SubmitAsync(CancellationToken cancellationToken = default)
    {
        // Update internal state first to prevent race conditions
        Status = OrderStatus.NewRequest;

        var request = new NewOrderRequest(InstrumentId, ClientOrderId, Side, Price, Quantity, OrderType);
        var result = await _gateway.SendNewOrderAsync(request, cancellationToken);

        if (!result.IsSuccess)
        {
            // If the request fails immediately, create a rejection report.
            var failureReport = new OrderStatusReport(
                ClientOrderId, null, InstrumentId, OrderStatus.Rejected, Price, Quantity, Quantity,
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
    public Task ReplaceAsync(Price price, OrderType orderType, CancellationToken cancellationToken = default) { /* ... implementation ... */ return Task.CompletedTask; }
    public Task CancelAsync(CancellationToken cancellationToken = default) { /* ... implementation ... */ return Task.CompletedTask; }

    private static long GenerateClientId() => DateTimeOffset.UtcNow.Ticks; // Placeholder

    public void OnStatusReportReceived(in OrderStatusReport report)
    {
        Status = report.Status;
        LeavesQuantity = report.LeavesQuantity;
        LastUpdateTime = report.Timestamp;
        LatestReport = report;
        if (!string.IsNullOrEmpty(report.ExchangeOrderId))
        {
            ExchangeOrderId = report.ExchangeOrderId;
        }

        StatusChanged?.Invoke(this, report);

        switch (report.Status)
        {
            case OrderStatus.Filled:
            case OrderStatus.Cancelled:
            case OrderStatus.Rejected:
                _router.DeregisterOrder(this);
                break;
        }
    }
}