using System;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.FairValue;

namespace OpenHFT.Quoting;

/// <summary>
/// Contains the parameters used to generate quotes around a fair value.
/// This is an immutable value type.
/// </summary>
public readonly struct QuotingParameters : IEquatable<QuotingParameters>
{
    public readonly int InstrumentId { get; }

    public readonly FairValueModel FvModel { get; }
    /// <summary>
    /// spread ratio in bp to calculate quotes on each side from fair value.
    // Price of ask quote = fair value * (1 + SpreadBp * 1e-4 / 2 )
    /// Price of bid quote = fair value * (1 - SpreadBp * 1e-4 / 2 )
    /// </summary>
    public readonly decimal SpreadBp { get; }

    /// <summary>
    /// skew ratio in bp to calculate skewed fair value to hedge inventory risk
    /// </summary>
    public readonly decimal SkewBp { get; }

    /// <summary>
    /// The size of the quotes to be placed per each price level.
    /// </summary>
    public readonly Quantity Size { get; }

    /// <summary>
    /// # of price level on each side to quote
    /// </summary>
    public readonly int Depth { get; }

    /// <summary>
    /// Initializes a new instance of the QuotingParameters struct with all required values.
    /// </summary>
    /// <param name="instrumentId">The ID of the instrument these parameters apply to.</param>
    /// <param name="fvModel">The fair value model to be used.</param>
    /// <param name="spreadBp">The spread in basis points.</param>
    /// <param name="skewBp">The skew in basis points.</param>
    /// <param name="size">The quantity for each quote level.</param>
    /// <param name="depth">The number of quote levels on each side.</param>
    public QuotingParameters(int instrumentId, FairValueModel fvModel, decimal spreadBp, decimal skewBp, Quantity size, int depth)
    {
        InstrumentId = instrumentId;
        FvModel = fvModel;
        SpreadBp = spreadBp;
        SkewBp = skewBp;
        Size = size;
        Depth = depth;
    }

    public override string ToString()
    {
        return $"{{ " +
               $"\"InstrumentId\": {InstrumentId}, " +
               $"\"SpreadBp\": {SpreadBp}, " +
               $"\"SkewBp\": {SkewBp}, " +
               $"\"Size\": {Size.ToDecimal()}, " +
               $"\"Depth\": {Depth}" +
               $" }}";
    }

    public bool Equals(QuotingParameters other)
    {
        return InstrumentId == other.InstrumentId &&
               FvModel == other.FvModel &&
               SpreadBp == other.SpreadBp &&
               SkewBp == other.SkewBp &&
               Size == other.Size &&
               Depth == other.Depth;

    }
    public override bool Equals(object? obj)
    {
        return obj is QuotingParameters other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(InstrumentId);
        hash.Add(FvModel);
        hash.Add(SpreadBp);
        hash.Add(SkewBp);
        hash.Add(Size);
        hash.Add(Depth);
        return hash.ToHashCode();
    }

    public static bool operator ==(QuotingParameters left, QuotingParameters right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(QuotingParameters left, QuotingParameters right)
    {
        return !left.Equals(right);
    }
}