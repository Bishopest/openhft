using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Processing;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting.Interfaces;

/// <summary>
/// Defines the contract for a quoting engine for a single instrument.
/// </summary>
public interface IQuotingEngine
{
    Instrument QuotingInstrument { get; }
    bool IsActive { get; }
    QuotingParameters CurrentParameters { get; }
    event EventHandler<QuotePair> QuotePairCalculated;
    void Start();
    void Stop();
    void Activate();
    void Deactivate();
    void UpdateParameters(QuotingParameters newParameters);
}