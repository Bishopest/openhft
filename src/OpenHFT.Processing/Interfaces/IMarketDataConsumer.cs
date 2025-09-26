using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Processing.Interfaces;

public interface IMarketDataConsumer
{
    Task OnMarketData(MarketDataEvent marketEvent);
    string ConsumerName { get; }
    int SymbolId { get; }
    ExchangeEnum Exchange { get; }
    int Priority { get; } // Lower numbers = higher priority
}