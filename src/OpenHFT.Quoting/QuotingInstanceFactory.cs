using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Processing;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Validators;

namespace OpenHFT.Quoting;

public class QuotingInstanceFactory : IQuotingInstanceFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMarketDataManager _marketDataManager;
    private readonly IFairValueProviderFactory _fairValueProviderFactory;
    private readonly IQuoterFactory _quoterFactory;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly IFxRateService _fxRateManager;


    public QuotingInstanceFactory(
        ILoggerFactory loggerFactory,
        IMarketDataManager marketDataManager,
        IFairValueProviderFactory fairValueProviderFactory,
        IQuoterFactory quoterFactory,
        IInstrumentRepository instrumentRepository,
        IFxRateService fxRateManager)
    {
        _loggerFactory = loggerFactory;
        _marketDataManager = marketDataManager;
        _fairValueProviderFactory = fairValueProviderFactory;
        _quoterFactory = quoterFactory;
        _instrumentRepository = instrumentRepository;
        _fxRateManager = fxRateManager;
    }

    public QuotingInstance? Create(QuotingParameters parameters)
    {
        var instrument = _instrumentRepository.GetById(parameters.InstrumentId);
        var fvInstrument = _instrumentRepository.GetById(parameters.FairValueSourceInstrumentId);
        if (instrument == null || fvInstrument == null)
        {
            _loggerFactory.CreateLogger<QuotingInstanceFactory>().LogWarningWithCaller($"Quoting Instrument with ID {parameters.InstrumentId} or FV Instrument with ID {parameters.FairValueSourceInstrumentId} not found.");
            return null;
        }

        var bidQuoter = _quoterFactory.CreateQuoter(parameters, Side.Buy);
        var askQuoter = _quoterFactory.CreateQuoter(parameters, Side.Sell);
        bidQuoter.UpdateParameters(parameters);
        askQuoter.UpdateParameters(parameters);

        var validator = new DefaultQuoteValidator(_loggerFactory.CreateLogger<DefaultQuoteValidator>());

        var fvProvider = _fairValueProviderFactory.CreateProvider(parameters.FvModel, parameters.FairValueSourceInstrumentId);
        var mm = new MarketMaker(_loggerFactory.CreateLogger<MarketMaker>(),
            instrument,
            bidQuoter,
            askQuoter,
            validator);

        IFxConverter fxConverter;
        if (FxRateManagerBase.IsEquivalent(instrument.QuoteCurrency, fvInstrument.QuoteCurrency))
        {
            fxConverter = new NullFxConverter();
            _loggerFactory.CreateLogger<QuotingInstanceFactory>().LogInformationWithCaller($"Quote currencies for {instrument.Symbol} are equivalent. Using NullFxConverter.");
        }
        else
        {
            _loggerFactory.CreateLogger<QuotingInstanceFactory>().LogInformationWithCaller($"Quote currencies for {instrument.Symbol} differ ({fvInstrument.QuoteCurrency} -> {instrument.QuoteCurrency}). Setting up FxConverter.");
            var unitAmount = new CurrencyAmount(1m, fvInstrument.QuoteCurrency);
            var convertedAmount = _fxRateManager.Convert(unitAmount, instrument.QuoteCurrency);

            if (convertedAmount.HasValue && convertedAmount.Value.Amount > 0)
            {
                decimal fixedRate = convertedAmount.Value.Amount;
                _loggerFactory.CreateLogger<QuotingInstanceFactory>().LogInformationWithCaller($"Initialized FixedFxConverter for {instrument.Symbol} with rate: 1 {fvInstrument.QuoteCurrency} = {fixedRate} {instrument.QuoteCurrency}");
                fxConverter = new FixedFxConverter(fixedRate);
            }
            else
            {
                _loggerFactory.CreateLogger<QuotingInstanceFactory>().LogWarningWithCaller($"Could not fetch initial fixed FX rate for {instrument.Symbol}. Falling back to RealtimeFxConverter.");
                fxConverter = new RealtimeFxConverter(
                    _fxRateManager,
                    _loggerFactory.CreateLogger<RealtimeFxConverter>()
                );
            }
        }

        var engine = new QuotingEngine(_loggerFactory.CreateLogger<QuotingEngine>(), instrument, fvInstrument, mm, fvProvider, parameters, _marketDataManager, fxConverter);

        mm.SetQuotingStateProvider(engine);
        mm.OrderFullyFilled += () => engine.PauseQuoting(TimeSpan.FromSeconds(3));

        return new QuotingInstance(engine);
    }
}
