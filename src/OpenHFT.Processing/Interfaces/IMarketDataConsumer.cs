using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Processing.Interfaces;

public interface IMarketDataConsumer
{
    Task OnMarketData(MarketDataEvent marketEvent);
    string ConsumerName { get; }
    int InstrumentId { get; }
    ExchangeEnum Exchange { get; }
    int Priority { get; } // Lower numbers = higher priority
}