using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Quoting.FairValue;

namespace OpenHFT.Quoting.Interfaces;

/// <summary>
/// Defines the contract for a quoting engine for a single instrument.
/// </summary>
public interface IQuotingEngine
{
    Instrument QuotingInstrument { get; }
    void Start();
    void Stop();
    void UpdateParameters(QuotingParameters newParameters);
    void SetFairValueProvider(FairValueModel model);
}