using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting.FairValue;

/// <summary>
/// A Fair Value Provider that calculates the fair value as the simple mid-price
/// (average of best bid and best ask) from the raw order book data.
/// </summary>
public class MidpFairValueProvider : AbstractFairValueProvider, IOrderBookConsumerProvider
{
    public override FairValueModel Model => FairValueModel.Midp;

    public MidpFairValueProvider(ILogger logger, int instrumentId) : base(logger, instrumentId)
    {
    }

    public void Update(OrderBook ob)
    {
        if (ob.InstrumentId != SourceInstrumentId) return;

        var midP = ob.GetMidPrice();

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
