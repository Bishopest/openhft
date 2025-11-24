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

    public QuoterFactory(ILoggerFactory loggerFactory, IOrderFactory orderFactory)
    {
        _loggerFactory = loggerFactory;
        _orderFactory = orderFactory;
    }

    public IQuoter CreateQuoter(Instrument instrument, Side side, QuoterType type, string? bookName = null)
    {
        _loggerFactory.CreateLogger<QuoterFactory>().LogInformationWithCaller($"Creating quoter of type {type} for instrument {instrument.Symbol} on side {side}.");
        switch (type)
        {
            case QuoterType.Log:
                return new LogQuoter(_loggerFactory.CreateLogger<LogQuoter>());
            case QuoterType.Single:
                if (bookName == null) throw new ArgumentNullException("bookName");
                return new SingleOrderQuoter(_loggerFactory.CreateLogger<SingleOrderQuoter>(), side, instrument, _orderFactory, bookName);
            case QuoterType.Grouped:
                if (bookName == null) throw new ArgumentNullException("bookName");
                return new GroupedSingleOrderQuoter(_loggerFactory.CreateLogger<GroupedSingleOrderQuoter>(), side, instrument, _orderFactory, bookName);
            default:
                throw new ArgumentException($"Unsupported quoter type: {type}");
        }
    }
}
