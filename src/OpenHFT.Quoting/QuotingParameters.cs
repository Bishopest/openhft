using System.Text.Json.Serialization;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting;

/// <summary>
/// Contains the parameters used to generate quotes around a fair value.
/// This is an immutable value type.
/// </summary>
public readonly struct QuotingParameters : IEquatable<QuotingParameters>
{
    public readonly int InstrumentId { get; }

    public readonly FairValueModel FvModel { get; }

    public readonly int FairValueSourceInstrumentId { get; }

    /// <summary>
    /// If true, all limit orders will be submitted as Post-Only.
    /// A Post-Only order is rejected if it would execute immediately as a taker.
    /// </summary>
    public readonly bool PostOnly { get; }

    /// <summary>
    /// spread ratio in bp to calculate quotes on each side from fair value.
    /// Price of ask quote = fair value * (1 + AskSpreadBp * 1e-4 )
    /// Price of bid quote = fair value * (1 + BidSpreadBp * 1e-4 )
    /// </summary>
    public readonly decimal AskSpreadBp { get; }
    public readonly decimal BidSpreadBp { get; }

    /// <summary>
    /// skew ratio in bp to calculate skewed fair value to hedge inventory risk
    /// </summary>
    public readonly decimal SkewBp { get; }

    /// <summary>
    /// The size of the quotes to be placed per each price level.
    /// </summary>
    public readonly Quantity Size { get; }

    /// <summary>
    /// The maximum cumulative net long fills allowed.
    /// If the current cumulative net long fills exceeds this value, new bid quotes will be held (not placed).
    /// </summary>
    public readonly Quantity MaxCumBidFills { get; }

    /// <summary>
    /// The maximum cumulative net short position allowed (as a positive number).
    /// If the absolute value of the current cumulative net short fills exceeds this value, new ask quotes will be held.
    /// </summary>
    public readonly Quantity MaxCumAskFills { get; }

    /// <summary>
    /// # of price level on each side to quote
    /// </summary>
    public readonly int Depth { get; }

    /// <summary>
    /// Specifies the type of quoter to be used for this instance.
    /// </summary>
    public readonly QuoterType Type { get; }
    /// <summary>
    /// Initializes a new instance of the QuotingParameters struct with all required values.
    /// </summary>
    /// <param name="instrumentId">The ID of the instrument these parameters apply to.</param>
    /// <param name="fvModel">The fair value model to be used.</param>
    /// <param name="fairValueSourceInstrumentId"></param>
    /// <param name="spreadBp">The spread in basis points.</param>
    /// <param name="skewBp">The skew in basis points.</param>
    /// <param name="size">The quantity for each quote level.</param>
    /// <param name="depth">The number of quote levels on each side.</param>
    /// <param name="type">The type of quoter to be used.</param>
    /// <param name="postOnly">If true, all limit orders will be submitted as Post-Only.</param>
    [JsonConstructor]
    public QuotingParameters(
        int instrumentId,
        FairValueModel fvModel,
        int fairValueSourceInstrumentId,
        decimal askSpreadBp,
        decimal bidSpreadBp,
        decimal skewBp,
        Quantity size,
        int depth,
        QuoterType type,
        bool postOnly,
        Quantity maxCumBidFills,
        Quantity maxCumAskFills)
    {
        if (askSpreadBp <= bidSpreadBp)
        {
            throw new ArgumentException("AskSpreadBp must be strictly greater than BidSpreadBp to ensure a positive spread.",
                                        nameof(QuotingParameters));
        }

        if (maxCumBidFills.ToDecimal() <= 0 || maxCumAskFills.ToDecimal() <= 0)
        {
            throw new ArgumentException("MaxCumBidFills and MaxCumAskFills must be greater than zero.",
                                        nameof(QuotingParameters));
        }

        InstrumentId = instrumentId;
        FvModel = fvModel;
        FairValueSourceInstrumentId = fairValueSourceInstrumentId;
        AskSpreadBp = askSpreadBp;
        BidSpreadBp = bidSpreadBp;
        SkewBp = skewBp;
        Size = size;
        Depth = depth;
        Type = type;
        PostOnly = postOnly;
        MaxCumBidFills = maxCumBidFills;
        MaxCumAskFills = maxCumAskFills;
    }

    public override string ToString()
    {
        return $"{{ " +
               $"\"InstrumentId\": {InstrumentId}, " +
               $"\"AskSpreadBp\": {AskSpreadBp}, " +
               $"\"BidSpreadBp\": {BidSpreadBp}, " +
               $"\"SkewBp\": {SkewBp}, " +
               $"\"Size\": {Size.ToDecimal()}, " +
               $"\"Depth\": {Depth}" +
               $"\"Type\": {Type}" +
               $"\"PostOnly\": {PostOnly}" +
               $"\"MaxCumBidFills\": {MaxCumBidFills.ToDecimal()}, " +
               $"\"MaxCumAskFills\": {MaxCumAskFills.ToDecimal()}, " +
               $" }}";
    }

    public bool Equals(QuotingParameters other)
    {
        return InstrumentId == other.InstrumentId &&
               FvModel == other.FvModel &&
               AskSpreadBp == other.AskSpreadBp &&
               BidSpreadBp == other.BidSpreadBp &&
               SkewBp == other.SkewBp &&
               Size == other.Size &&
               Depth == other.Depth &&
               PostOnly == other.PostOnly &&
               Type == other.Type &&
               MaxCumBidFills == other.MaxCumBidFills &&
               MaxCumAskFills == other.MaxCumAskFills;
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
        hash.Add(AskSpreadBp);
        hash.Add(BidSpreadBp);
        hash.Add(SkewBp);
        hash.Add(Size);
        hash.Add(Depth);
        hash.Add(Type);
        hash.Add(PostOnly);
        hash.Add(MaxCumBidFills);
        hash.Add(MaxCumAskFills);
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