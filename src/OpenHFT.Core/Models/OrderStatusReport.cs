using System;
using System.Text.Json.Serialization;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Models;

/// <summary>
/// Represents a report from the exchange regarding the status of a specific order.
/// This is an immutable, GC-free Data Transfer Object (DTO) used for communication
/// between the exchange adapter and the order management logic.
/// </summary>
public readonly struct OrderStatusReport
{
    /// <summary>
    /// The client-side order ID used to correlate this report with an IOrder instance.
    /// </summary>
    public readonly long ClientOrderId { get; }

    /// <summary>
    /// The exchange-assigned order ID. This may be null in early reports (e.g., acknowledgment).
    /// </summary>
    public readonly string? ExchangeOrderId { get; }

    /// <summary>
    /// A unique identifier for this specific execution, assigned by the exchange.
    /// </summary>
    public readonly string? ExecutionId { get; }

    /// <summary>
    /// The unique identifier for the instrument.
    /// </summary>
    public readonly int InstrumentId { get; }

    /// <summary>
    /// The side of the order (e.g., Buy, Sell).
    /// </summary>
    public readonly Side Side { get; }

    /// <summary>
    /// The new status of the order (e.g., New, Filled, Cancelled).
    /// </summary>
    public readonly OrderStatus Status { get; }

    /// <summary>
    /// The price associated with this report
    /// </summary>
    public readonly Price Price { get; }

    /// <summary>
    /// The original quantity associated with this report
    /// </summary>
    public readonly Quantity Quantity { get; }

    /// <summary>
    /// The quantity remaining on the order after this event.
    /// </summary>
    public readonly Quantity LeavesQuantity { get; }

    /// <summary>
    /// The quantity filled at this event. null if not yet filled.
    /// </summary>
    public readonly Quantity? LastQuantity { get; }

    /// <summary>
    /// The price filled at this event. null if not yet filled.
    /// </summary>
    public readonly Price? LastPrice { get; }


    /// <summary>
    /// The UTC timestamp in Unix milliseconds when the event occurred at the exchange.
    /// </summary>
    public readonly long Timestamp { get; }

    /// <summary>
    /// A human-readable reason for rejection, if applicable.
    /// </summary>
    public readonly string? RejectReason { get; }

    [JsonConstructor]
    public OrderStatusReport(
        long clientOrderId,
        string? exchangeOrderId,
        string? executionId,
        int instrumentId,
        Side side,
        OrderStatus status,
        Price price,
        Quantity quantity,
        Quantity leavesQuantity,
        long timestamp,
        string? rejectReason = null,
        Quantity? lastQuantity = null,
        Price? lastPrice = null)
    {
        ClientOrderId = clientOrderId;
        ExchangeOrderId = exchangeOrderId;
        ExecutionId = executionId;
        InstrumentId = instrumentId;
        Side = side;
        Status = status;
        Price = price;
        Quantity = quantity;
        LeavesQuantity = leavesQuantity;
        LastQuantity = lastQuantity;
        LastPrice = lastPrice;
        Timestamp = timestamp;
        RejectReason = rejectReason;
    }

    public override string ToString()
    {
        return $"{{ " +
               $"\"ClientOrderId\": {ClientOrderId}, " +
               $"\"ExchangeOrderId\": \"{ExchangeOrderId}\", " +
               $"\"ExecutionId\": \"{ExecutionId}\", " +
               $"\"InstrumentId\": {InstrumentId}, " +
               $"\"Side\": {Side}, " +
               $"\"Status\": {Status}, " +
               $"\"Price\": {Price.ToDecimal()}, " +
               $"\"Quantity\": {Quantity.ToDecimal()}, " +
               $"\"LastQuantity\": {LastQuantity?.ToDecimal()}, " +
               $"\"LastPrice\": {LastPrice?.ToDecimal()}, " +
               $"\"LeavesQuantity\": {LeavesQuantity.ToDecimal()}, " +
               $"\"Timestamp\": {Timestamp}, " +
               $"\"RejectReason\": \"{RejectReason}\"" +
               $" }}";
    }
}