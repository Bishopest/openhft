using System;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.FairValue;

namespace OpenHFT.Quoting;

/// <summary>
/// Contains the parameters used to generate quotes around a fair value.
/// This is an immutable value type.
/// </summary>
public readonly struct QuotingParameters
{
    public readonly int InstrumentId { get; }

    public readonly FairValueModel FvModel { get; }
    /// <summary>
    /// spread ratio in bp to calculate quotes on each side from fair value.
    /// Price of ask quote = fair value * (1 + SpreadBp * 1e-4 / 2 )
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

    // Add other parameters as needed, e.g., skew, volatility adjustments, etc.

    public QuotingParameters(decimal spreadBp, Quantity size)
    {
        SpreadBp = spreadBp;
        Size = size;
    }
}