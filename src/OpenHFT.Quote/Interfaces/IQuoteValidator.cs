using System;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting.Interfaces;

// Abstracts the business logic for quote validation.
public interface IQuoteValidator
{
    /// <summary>
    /// Determines if a given quote is valid to be placed on the market.
    /// For example, it checks if the quote would cross the spread.
    /// </summary>
    /// <param name="quote">The quote to validate.</param>
    /// <param name="side">The side of the quote.</param>
    /// <returns>True if the quote is valid (Live), false otherwise (Held).</returns>
    bool ShouldQuoteBeLive(Quote quote, Side side);
}
