using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting;

public class FixedFxConverter : IFxConverter
{
    private readonly decimal _fixedRate;
    // The constructor takes the fixed conversion rate.
    public FixedFxConverter(decimal fixedRate)
    {
        _fixedRate = fixedRate;
    }

    public FairValueUpdate? Convert(FairValueUpdate sourceUpdate, Currency sourceCurrency, Currency targetCurrency)
    {
        if (FxRateManagerBase.IsEquivalent(sourceCurrency, targetCurrency))
        {
            return sourceUpdate;
        }

        var convertedAsk = sourceUpdate.FairAskValue.ToDecimal() * _fixedRate;
        var convertedBid = sourceUpdate.FairBidValue.ToDecimal() * _fixedRate;

        return new FairValueUpdate(
            sourceUpdate.InstrumentId,
            Price.FromDecimal(convertedAsk),
            Price.FromDecimal(convertedBid)
        );
    }
}