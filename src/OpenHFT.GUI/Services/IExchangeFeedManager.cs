using System;
using OpenHFT.Core.Models;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.GUI.Services;

public interface IExchangeFeedManager
{
    event EventHandler<MarketDataEvent> OnMarketDataReceived;
    event EventHandler<ConnectionStateChangedEventArgs>? AdapterConnectionStateChanged;
    Task SubscribeToInstrumentsAsync(IEnumerable<int> instrumentIds);
    Task UnsubscribeFromInstrumentsAsync(IEnumerable<int> instrumentIds);
}
