using System;
using OpenHFT.Core.Models;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.GUI.Services;

public interface IExchangeFeedManager
{
    event EventHandler<MarketDataEvent> OnMarketDataReceived;
    event EventHandler<ConnectionStateChangedEventArgs>? AdapterConnectionStateChanged;
    Task SubscribeToInstrumentAsync(int instrumentId);
    Task UnsubscribeFromInstrumentAsync(int instrumentId);
}
