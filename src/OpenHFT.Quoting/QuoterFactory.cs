using System;
using Microsoft.Extensions.Logging;
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
    private readonly IOrderGatewayRegistry _orderGatewayRegistry;


    public QuoterFactory(ILoggerFactory loggerFactory, IOrderFactory orderFactory, IMarketDataManager marketDataManager, IInstrumentRepository repository, IOrderGatewayRegistry orderGatewayRegistry)
    {
        _loggerFactory = loggerFactory;
        _orderFactory = orderFactory;
        _marketDataManager = marketDataManager;
        _repository = repository;
        _orderGatewayRegistry = orderGatewayRegistry;
    }

    public IQuoter CreateQuoter(QuotingParameters parameters, Side side)
    {
        var instrument = _repository.GetById(parameters.InstrumentId);
        if (instrument == null)
        {
            _loggerFactory.CreateLogger<QuoterFactory>().LogWarningWithCaller($"Instrument with ID {parameters.InstrumentId} not found.");
            throw new ArgumentNullException("instrument not found when creating quoter");
        }

        _loggerFactory.CreateLogger<QuoterFactory>().LogInformationWithCaller($"Creating quoter of type {parameters.AskQuoterType} for instrument {instrument.Symbol} on side {side}.");
        var sidedQuoterType = side == Side.Sell ? parameters.AskQuoterType : parameters.BidQuoterType;
        switch (sidedQuoterType)
        {
            case QuoterType.Log:
                return new LogQuoter(_loggerFactory.CreateLogger<LogQuoter>());
            case QuoterType.Single:
                if (parameters.BookName == null) throw new ArgumentNullException("bookName");
                return new SingleOrderQuoter(_loggerFactory.CreateLogger<SingleOrderQuoter>(), side, instrument, _orderFactory, parameters.BookName, _marketDataManager);
            case QuoterType.Multi:
                if (parameters.BookName == null) throw new ArgumentNullException("bookName");
                var gatewayForMulti = _orderGatewayRegistry.GetGatewayForInstrument(instrument.InstrumentId);
                if (gatewayForMulti == null) throw new ArgumentNullException("gateway");
                return new MultiOrderQuoter(_loggerFactory.CreateLogger<MultiOrderQuoter>(), side, instrument, _orderFactory, gatewayForMulti, parameters.BookName, _marketDataManager, parameters);
            case QuoterType.Layered:
                if (parameters.BookName == null) throw new ArgumentNullException("bookName");
                var gatewayForLayered = _orderGatewayRegistry.GetGatewayForInstrument(instrument.InstrumentId);
                if (gatewayForLayered == null) throw new ArgumentNullException("gateway");
                return new LayeredQuoter(_loggerFactory.CreateLogger<LayeredQuoter>(), side, instrument, _orderFactory, gatewayForLayered, parameters.BookName, _marketDataManager, parameters);
            // QuoterFactory.cs 내부 switch 문에 추가
            case QuoterType.Shadow:
                if (parameters.BookName == null) throw new ArgumentNullException("bookName");
                return new ShadowQuoter(_loggerFactory.CreateLogger<ShadowQuoter>(), side, instrument, _orderFactory, parameters.BookName, _marketDataManager);
            case QuoterType.ShadowMaker:
                if (parameters.BookName == null) throw new ArgumentNullException("bookName");
                return new ShadowMakerQuoter(_loggerFactory.CreateLogger<ShadowQuoter>(), side, instrument, _orderFactory, parameters.BookName, _marketDataManager);
            default:
                throw new ArgumentException($"Unsupported quoter type: {parameters.AskQuoterType}");
        }
    }
}
