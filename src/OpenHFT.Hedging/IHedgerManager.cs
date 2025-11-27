using System;

namespace OpenHFT.Hedging;

public interface IHedgerManager : IDisposable
{
    event EventHandler<HedgingParameters> HedgingParametersUpdated;
    bool UpdateHedgingParameters(HedgingParameters parameters);
    bool StopHedging(int quotingInstrumentId);
    Hedger? GetHedger(int quotingInstrumentId);
    IReadOnlyCollection<Hedger> GetAllHedgers();
}
