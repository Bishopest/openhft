using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting;

public class RealtimeFxConverter : IFxConverter
{
    private readonly IFxRateService _fxRateManager;
    private readonly ILogger<RealtimeFxConverter> _logger;

    public RealtimeFxConverter(IFxRateService fxRateManager, ILogger<RealtimeFxConverter> logger)
    {
        _fxRateManager = fxRateManager;
        _logger = logger;
    }

    public FairValueUpdate? Convert(FairValueUpdate sourceUpdate, Currency sourceCurrency, Currency targetCurrency)
    {
        if (FxRateManagerBase.IsEquivalent(sourceCurrency, targetCurrency))
        {
            return sourceUpdate;
        }

        var fvAskInSource = new CurrencyAmount(sourceUpdate.FairAskValue.ToDecimal(), sourceCurrency);
        var fvBidInSource = new CurrencyAmount(sourceUpdate.FairBidValue.ToDecimal(), sourceCurrency);

        var convertedAsk = _fxRateManager.Convert(fvAskInSource, targetCurrency);
        var convertedBid = _fxRateManager.Convert(fvBidInSource, targetCurrency);

        if (convertedAsk == null || convertedBid == null)
        {
            _logger.LogWarningWithCaller($"Failed to get real-time FX rate for {sourceCurrency} -> {targetCurrency}.");
            return null;
        }

        return new FairValueUpdate(
            sourceUpdate.InstrumentId,
            Price.FromDecimal(convertedAsk.Value.Amount),
            Price.FromDecimal(convertedBid.Value.Amount)
        );
    }
}
