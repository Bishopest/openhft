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
    private readonly MarketDataManager _marketDataManager;
    private readonly IFairValueProviderFactory _fairValueProviderFactory;
    private readonly IQuoterFactory _quoterFactory;
    private readonly IInstrumentRepository _instrumentRepository;

    public QuotingInstanceFactory(
        ILoggerFactory loggerFactory,
        MarketDataManager marketDataManager,
        IFairValueProviderFactory fairValueProviderFactory,
        IQuoterFactory quoterFactory,
        IInstrumentRepository instrumentRepository)
    {
        _loggerFactory = loggerFactory;
        _marketDataManager = marketDataManager;
        _fairValueProviderFactory = fairValueProviderFactory;
        _quoterFactory = quoterFactory;
        _instrumentRepository = instrumentRepository;
    }

    public QuotingInstance? Create(QuotingParameters parameters)
    {
        var instrument = _instrumentRepository.GetById(parameters.InstrumentId);
        if (instrument == null)
        {
            _loggerFactory.CreateLogger<QuotingInstanceFactory>().LogWarningWithCaller($"Instrument with ID {parameters.InstrumentId} not found.");
            return null;
        }
        var bidQuoter = _quoterFactory.CreateQuoter(instrument, Side.Buy, parameters.Type);
        var askQuoter = _quoterFactory.CreateQuoter(instrument, Side.Sell, parameters.Type);
        var validator = new DefaultQuoteValidator(_loggerFactory.CreateLogger<DefaultQuoteValidator>());

        var mm = new MarketMaker(_loggerFactory.CreateLogger<MarketMaker>(),
            instrument,
            bidQuoter,
            askQuoter,
            validator);
        var fvProvider = _fairValueProviderFactory.CreateProvider(parameters.FvModel, parameters.FairValueSourceInstrumentId);
        var engine = new QuotingEngine(_loggerFactory.CreateLogger<QuotingEngine>(), instrument, mm, fvProvider, parameters, _marketDataManager);
        mm.OrderFullyFilled += () => engine.PauseQuoting(TimeSpan.FromSeconds(3));

        return new QuotingInstance(engine);
    }
}
