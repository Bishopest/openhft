using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting;

public class QuoterFactory : IQuoterFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOrderFactory _orderFactory;
    private readonly IMarketDataManager _marketDataManager;
    private readonly IInstrumentRepository _repository;

    public QuoterFactory(ILoggerFactory loggerFactory, IOrderFactory orderFactory, IMarketDataManager marketDataManager, IInstrumentRepository repository)
    {
        _loggerFactory = loggerFactory;
        _orderFactory = orderFactory;
        _marketDataManager = marketDataManager;
        _repository = repository;
    }

    public IQuoter CreateQuoter(QuotingParameters parameters, Side side)
    {
        var instrument = _repository.GetById(parameters.InstrumentId);
        if (instrument == null)
        {
            _loggerFactory.CreateLogger<QuoterFactory>().LogWarningWithCaller($"Instrument with ID {parameters.InstrumentId} not found.");
            throw new ArgumentNullException("instrument not found when creating quoter");
        }

        _loggerFactory.CreateLogger<QuoterFactory>().LogInformationWithCaller($"Creating quoter of type {parameters.Type} for instrument {instrument.Symbol} on side {side}.");
        switch (parameters.Type)
        {
            case QuoterType.Log:
                return new LogQuoter(_loggerFactory.CreateLogger<LogQuoter>());
            case QuoterType.Single:
                if (parameters.BookName == null) throw new ArgumentNullException("bookName");
                return new SingleOrderQuoter(_loggerFactory.CreateLogger<SingleOrderQuoter>(), side, instrument, _orderFactory, parameters.BookName, _marketDataManager);
            default:
                throw new ArgumentException($"Unsupported quoter type: {parameters.Type}");
        }
    }
}
