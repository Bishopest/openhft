using System.Text.Json.Serialization;
using OpenHFT.Core.Models;

namespace OpenHFT.Hedging;

public readonly struct HedgingParameters : IEquatable<HedgingParameters>
{
    public readonly int QuotingInstrumentId { get; }
    public readonly int InstrumentId { get; }
    public readonly HedgeOrderType OrderType { get; }
    public readonly Quantity Size { get; }

    /// <summary>
    /// Initializes a new instance of the HedgingParameters struct with all required values.
    /// </summary>
    /// <param name="quotingInstrumentId">The ID of the quoting instrument to be hedged</param>
    /// <param name="instrumentId">The ID of the instrument these parameters apply to.</param>
    /// <param name="orderType">Hedge order type</param>
    /// <param name="size">The quantity for each hedge order.</param>
    [JsonConstructor]
    public HedgingParameters(
        int quotingInstrumentId,
        int instrumentId,
        HedgeOrderType orderType,
        Quantity size)
    {
        QuotingInstrumentId = quotingInstrumentId;
        InstrumentId = instrumentId;
        OrderType = orderType;
        Size = size;
    }

    public override string ToString()
    {
        return $"{{ " +
               $"\"QuotingInstrumentId\": {QuotingInstrumentId}, " +
               $"\"InstrumentId\": {InstrumentId}, " +
               $"\"Ordertype\": {OrderType}, " +
               $"\"Size\": {Size.ToDecimal()}, " +
               $" }}";
    }

    public bool Equals(HedgingParameters other)
    {
        return QuotingInstrumentId == other.QuotingInstrumentId &&
               InstrumentId == other.InstrumentId &&
               OrderType == other.OrderType &&
               Size == other.Size;
    }
    public override bool Equals(object? obj)
    {
        return obj is HedgingParameters other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(QuotingInstrumentId);
        hash.Add(InstrumentId);
        hash.Add(OrderType);
        hash.Add(Size);
        return hash.ToHashCode();
    }

    public static bool operator ==(HedgingParameters left, HedgingParameters right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(HedgingParameters left, HedgingParameters right)
    {
        return !left.Equals(right);
    }
}
