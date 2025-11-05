using System;
using OpenHFT.Processing;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting;

public class QuotingInstance
{
    private readonly IQuotingEngine _engine;
    public IQuotingEngine Engine => _engine;

    public int InstrumentId => _engine.QuotingInstrument.InstrumentId;
    public bool IsActive => Engine.IsActive;

    public QuotingInstance(IQuotingEngine engine)
    {
        _engine = engine;
    }

    public void Start()
    {
        _engine.Start();
    }

    public void Stop()
    {
        _engine.Stop();
    }

    public void Activate() => _engine.Activate();
    public void Deactivate() => _engine.Deactivate();
}
