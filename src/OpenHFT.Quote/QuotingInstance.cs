using System;
using OpenHFT.Processing;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting;

public class QuotingInstance
{
    private readonly IQuotingEngine _engine;

    public int InstrumentId => _engine.QuotingInstrument.InstrumentId;

    public QuotingInstance(IQuotingEngine engine)
    {
        _engine = engine;
    }

    public void Start(MarketDataManager marketDataManager)
    {
        _engine.Start(marketDataManager);
    }

    public void Stop(MarketDataManager marketDataManager)
    {
        _engine.Stop(marketDataManager);
    }

    public bool TryGetEngine(out IQuotingEngine engine)
    {
        engine = _engine;
        return engine != null;
    }
}
