using System;
using OpenHFT.Core.Models;

namespace OpenHFT.GUI.Services;

public interface IExchangeFeedManager
{
    event EventHandler<MarketDataEvent> OnMarketDataReceived;
    Task SubscribeToInstrumentAsync(int instrumentId);
    Task UnsubscribeFromInstrumentAsync(int instrumentId);
}
