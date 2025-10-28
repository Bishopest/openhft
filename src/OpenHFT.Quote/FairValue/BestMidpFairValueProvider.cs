using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting.FairValue;

public class BestMidpFairValueProvider : AbstractFairValueProvider, IBestOrderBookConsumerProvider
{
    public override FairValueModel Model => FairValueModel.BestMidp;

    public BestMidpFairValueProvider(ILogger logger, int instrumentId) : base(logger, instrumentId)
    {
    }

    public void Update(BestOrderBook bob)
    {
        if (bob.InstrumentId != SourceInstrumentId) return;

        var midP = bob.GetMidPrice();

        if (midP.ToDecimal() == 0m)
        {
            return;
        }

        if (midP != _lastFairValue)
        {
            _lastFairValue = midP;
            OnFairValueChanged(new FairValueUpdate(SourceInstrumentId, midP));
        }
    }
}
