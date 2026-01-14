using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting;

/// <summary>
/// A "No-Op" implementation of IFxConverter that performs no conversion.
/// It is used when the source and target currencies are the same or equivalent.
/// </summary>
public class NullFxConverter : IFxConverter
{
    public FairValueUpdate? Convert(FairValueUpdate sourceUpdate, Currency sourceCurrency, Currency targetCurrency)
    {
        // Simply return the original update without any changes.
        return sourceUpdate;
    }
}