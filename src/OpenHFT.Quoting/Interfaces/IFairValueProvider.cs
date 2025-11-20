using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting.Interfaces;

/// <summary>
/// Represents an update to an instrument's calculated fair value.
/// </summary>
public readonly struct FairValueUpdate
{
    public readonly int InstrumentId;
    public readonly Price FairValue;
    public FairValueUpdate(int instrumentId, Price fairValue)
    {
        InstrumentId = instrumentId;
        FairValue = fairValue;
    }
}

/// <summary>
/// Defines the contract for a component that calculates the fair value of an instrument.
/// </summary>
public interface IFairValueProvider
{
    /// <summary>
    /// The instrument ID from which this provider derives its fair value.
    /// </summary>
    int SourceInstrumentId { get; }

    FairValueModel Model { get; }
    /// <summary>
    /// Fired whenever the calculated fair value changes.
    /// </summary>
    event EventHandler<FairValueUpdate> FairValueChanged;
}
