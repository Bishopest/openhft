using System;
using OpenHFT.Book.Core;
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

    FairValueModel Model { get; }
    /// <summary>
    /// Fired whenever the calculated fair value changes.
    /// </summary>
    event EventHandler<FairValueUpdate> FairValueChanged;

    /// <summary>
    /// Updates the provider with the latest market data (Top of Book).
    /// The implementation should decide if this new data results in a fair value change.
    /// </summary>
    /// <param name="topOfBook">The latest top-of-book data for the instrument.</param>
    void Update(OrderBook ob);
}
