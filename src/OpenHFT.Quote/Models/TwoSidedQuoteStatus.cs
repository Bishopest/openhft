using System;

namespace OpenHFT.Quoting.Models;

// Represents the status of both sides of the quote.
public readonly struct TwoSidedQuoteStatus : IEquatable<TwoSidedQuoteStatus>
{
    public readonly int InstrumentId { get; }
    public readonly QuoteStatus BidStatus { get; }
    public readonly QuoteStatus AskStatus { get; }

    public TwoSidedQuoteStatus(int instrumentId, QuoteStatus bidStatus, QuoteStatus askStatus)
    {
        InstrumentId = instrumentId;
        BidStatus = bidStatus;
        AskStatus = askStatus;
    }
    // Implement IEquatable for performance and correctness.
    public bool Equals(TwoSidedQuoteStatus other) =>
        InstrumentId == other.InstrumentId && BidStatus == other.BidStatus && AskStatus == other.AskStatus;
    public override bool Equals(object? obj) => obj is TwoSidedQuoteStatus other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(InstrumentId, BidStatus, AskStatus);
}
