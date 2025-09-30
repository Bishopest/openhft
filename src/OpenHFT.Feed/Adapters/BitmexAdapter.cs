using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed.Adapters;

public class BitmexAdapter : IFeedAdapter
{
    public ExchangeEnum SourceExchange => throw new NotImplementedException();

    public bool IsConnected => throw new NotImplementedException();

    public string Status => throw new NotImplementedException();

    public FeedStatistics Statistics => throw new NotImplementedException();

    public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;
    public event EventHandler<FeedErrorEventArgs> Error;
    public event EventHandler<MarketDataEvent> MarketDataReceived;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public Task SubscribeAsync(IEnumerable<Instrument> symbols, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task UnsubscribeAsync(IEnumerable<Instrument> symbols, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
