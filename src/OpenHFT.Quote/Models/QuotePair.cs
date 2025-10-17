using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting.Models;

/// <summary>
/// Represents a pair of bid and ask quotes that a market maker intends to submit to the exchange.
/// This is an immutable, GC-free value type representing a quoting intention.
/// </summary>
public readonly struct QuotePair : IEquatable<QuotePair>
{
    /// <summary>
    /// The unique identifier for the instrument being quoted.
    /// </summary>
    public readonly int InstrumentId { get; }

    /// <summary>
    /// The bid quote (buy side) to be submitted.
    /// </summary>
    public readonly Quote Bid { get; }

    /// <summary>
    /// The ask quote (sell side) to be submitted.
    /// </summary>
    public readonly Quote Ask { get; }

    /// <summary>
    /// The UTC timestamp in Unix milliseconds when this quote pair was created.
    /// This uses a common time reference, allowing comparison with market data timestamps.
    /// </summary>
    public readonly long CreationTimestamp { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="QuotePair"/> struct.
    /// </summary>
    /// <param name="instrumentId">The unique ID of the instrument.</param>
    /// <param name="bid">The bid quote to submit.</param>
    /// <param name="ask">The ask quote to submit.</param>
    /// /// <param name="creationTimestamp">The UTC timestamp in Unix milliseconds.</param>
    public QuotePair(int instrumentId, Quote bid, Quote ask, long creationTimestamp)
    {
        InstrumentId = instrumentId;
        Bid = bid;
        Ask = ask;
        CreationTimestamp = creationTimestamp;
    }

    /// <summary>
    /// Gets the intended spread of this quote pair.
    /// </summary>
    public Price Spread => Ask.Price - Bid.Price;


    /// <summary>
    /// Converts the Unix timestamp to a DateTimeOffset for display or interoperability.
    /// Note: Calling this property may cause heap allocation. Use with care in hot paths.
    /// </summary>
    public DateTimeOffset CreationTimestampAsDateTimeOffset => DateTimeOffset.FromUnixTimeMilliseconds(CreationTimestamp);
    // --- Equality Members ---

    public override bool Equals(object? obj) => obj is QuotePair other && Equals(other);
    public bool Equals(QuotePair other) =>
        InstrumentId == other.InstrumentId &&
        Bid.Equals(other.Bid) &&
        Ask.Equals(other.Ask) &&
        CreationTimestamp == other.CreationTimestamp;

    public override int GetHashCode() => HashCode.Combine(InstrumentId, Bid, Ask, CreationTimestamp);

    public static bool operator ==(QuotePair left, QuotePair right) => left.Equals(right);

    public static bool operator !=(QuotePair left, QuotePair right) => !left.Equals(right);

    public override string ToString() =>
        $"InstrumentId: {InstrumentId}, Bid: [{Bid}], Ask: [{Ask}], CreatedAt: {CreationTimestampAsDateTimeOffset:o}";
}