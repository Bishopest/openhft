using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting.FairValue;

public class OppositeBestFairValueProvider : AbstractFairValueProvider, IBestOrderBookConsumerProvider
{
    public override FairValueModel Model => FairValueModel.OppositeBest;
    public OppositeBestFairValueProvider(ILogger logger, int instrumentId) : base(logger, instrumentId)
    {
    }

    public void Update(BestOrderBook bob)
    {
        if (bob.InstrumentId != SourceInstrumentId) return;

        var bestAskPrice = bob.GetBestAsk().price;
        var bestBidPrice = bob.GetBestBid().price;

        if (bestAskPrice.ToDecimal() <= 0m || bestBidPrice.ToDecimal() <= 0m)
        {
            base.Logger.LogWarningWithCaller($"under zero best price on instrument id({bob.InstrumentId}).");
            return;
        }

        var fairValueUpdate = new FairValueUpdate(SourceInstrumentId, bestBidPrice, bestAskPrice);

        if (fairValueUpdate.FairAskValue != _lastFairAskValue || fairValueUpdate.FairBidValue != _lastFairBidValue)
        {
            _lastFairAskValue = fairValueUpdate.FairAskValue;
            _lastFairBidValue = fairValueUpdate.FairBidValue;
            OnFairValueChanged(fairValueUpdate);
        }
    }
}
