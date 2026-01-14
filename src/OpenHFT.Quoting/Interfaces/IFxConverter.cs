using System;
using OpenHFT.Core.Instruments;

namespace OpenHFT.Quoting.Interfaces;

/// <summary>
/// Defines a strategy for converting a FairValueUpdate from one currency to another.
/// </summary>
public interface IFxConverter
{
    /// <summary>
    /// Converts the given fair value update to the target currency if necessary.
    /// If no conversion is needed, it returns the original update.
    /// </summary>
    /// <param name="sourceUpdate">The original fair value update.</param>
    /// <param name="sourceCurrency">The quote currency of the source instrument.</param>
    /// <param name="targetCurrency">The quote currency of the instrument being quoted.</param>
    /// <returns>A converted FairValueUpdate, or null if conversion failed.</returns>
    FairValueUpdate? Convert(FairValueUpdate sourceUpdate, Currency sourceCurrency, Currency targetCurrency);
}
