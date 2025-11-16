using System;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting.Interfaces;

// Abstracts the business logic for quote validation.
public interface IQuoteValidator
{
    /// <summary>
    /// Determines if a given quote pair is valid to be placed on the market.
    /// For example, it checks if the quote would cross the spread.
    /// </summary>
    /// <param name="pair">The quote pair to validate.</param>
    /// <returns>Live if the quote is valid (does not cross), Held otherwise.</returns>
    TwoSidedQuoteStatus ShouldQuoteBeLive(QuotePair pair);
}
