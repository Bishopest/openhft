using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting.Interfaces;

public interface IFairValueProviderFactory
{
    /// <summary>
    /// Creates a new IFairValueProvider instance based on the specified model.
    /// </summary>
    /// <param name="model">The model type required for the provider (e.g., VWAP, MidPrice).</param>
    /// <param name="instrument">The instrument the provider will work with.</param>
    /// <returns>A new, initialized Fair Value Provider.</returns>
    IFairValueProvider CreateProvider(FairValueModel model, Instrument instrument);
}
