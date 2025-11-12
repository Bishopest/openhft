using System;
using System.Text.Json.Serialization;

namespace OpenHFT.Core.Models;

public readonly struct Fill : IEquatable<Fill>
{
    /// <summary>
    /// The unique identifier for the instrument that was traded.
    /// </summary>
    public readonly int InstrumentId { get; }

    /// <summary>
    /// The client-side order ID of the order that was filled.
    /// </summary>
    public readonly long ClientOrderId { get; }

    /// <summary>
    /// The exchange-assigned order ID.
    /// </summary>
    public readonly string ExchangeOrderId { get; }

    /// <summary>
    /// A unique identifier for this specific execution, assigned by the exchange.
    /// </summary>
    public readonly string ExecutionId { get; }

    /// <summary>
    /// The side of the order that was filled.
    /// </summary>
    public readonly Side Side { get; }

    /// <summary>
    /// The price at which the execution occurred.
    /// </summary>
    public readonly Price Price { get; }

    /// <summary>
    /// The quantity that was executed in this fill.
    /// </summary>
    public readonly Quantity Quantity { get; }

    /// <summary>
    /// The UTC timestamp in Unix milliseconds when the execution occurred.
    /// </summary>
    public readonly long Timestamp { get; }

    [JsonConstructor]
    public Fill(int instrumentId, long clientOrderId, string exchangeOrderId, string executionId, Side side, Price price, Quantity quantity, long timestamp)
    {
        InstrumentId = instrumentId;
        ClientOrderId = clientOrderId;
        ExchangeOrderId = exchangeOrderId;
        ExecutionId = executionId;
        Side = side;
        Price = price;
        Quantity = quantity;
        Timestamp = timestamp;
    }

    // --- IEquatable implementation ---
    public bool Equals(Fill other) => ExecutionId == other.ExecutionId && ExchangeOrderId == other.ExchangeOrderId;
    public override bool Equals(object? obj) => obj is Fill other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(ExchangeOrderId, ExecutionId);
    public static bool operator ==(Fill left, Fill right) => left.Equals(right);
    public static bool operator !=(Fill left, Fill right) => !left.Equals(right);

    public override string ToString() =>
        $"{Side} {Quantity.ToDecimal()} of Instrument {InstrumentId} @ {Price.ToDecimal()} (ExecID: {ExecutionId})";
}
