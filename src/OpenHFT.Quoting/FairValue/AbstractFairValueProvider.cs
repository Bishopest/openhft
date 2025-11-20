using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting.FairValue;

public abstract class AbstractFairValueProvider : IFairValueProvider
{
    protected readonly ILogger Logger;
    protected Price? _lastFairValue;
    public Price? LastFairValue => _lastFairValue;

    public abstract FairValueModel Model { get; }

    public int SourceInstrumentId { get; }

    public event EventHandler<FairValueUpdate>? FairValueChanged;

    protected AbstractFairValueProvider(ILogger logger, int instrumentId)
    {
        Logger = logger;
        SourceInstrumentId = instrumentId;
        _lastFairValue = null;
    }

    /// <summary>
    /// Helper method to safely invoke the event.
    /// </summary>
    protected virtual void OnFairValueChanged(FairValueUpdate update)
    {
        FairValueChanged?.Invoke(this, update);
    }
}
