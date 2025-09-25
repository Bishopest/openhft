using System;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Collections;
using OpenHFT.Core.Models;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed;

public class FeedChannelHandler : IFeedHandler
{
    private readonly ILogger<FeedChannelHandler> _logger;
    private readonly List<IFeedAdapter> _adapters = new();
    private readonly ChannelWriter<MarketDataEvent> _marketDataWriter;
    public IReadOnlyList<IFeedAdapter> Adapters => _adapters.AsReadOnly();
    public FeedHandlerStatistics Statistics { get; } = new();

    public event EventHandler<MarketDataEvent> MarketDataReceived;
    public event EventHandler<GapDetectedEventArgs> GapDetected;

    public FeedChannelHandler(ILogger<FeedChannelHandler> logger, ChannelWriter<MarketDataEvent> marketDataWriter, IEnumerable<IFeedAdapter> adapters)
    {
        _logger = logger;
        _marketDataWriter = marketDataWriter;
        _adapters.AddRange(adapters);
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var adapter in _adapters)
        {
            await adapter.ConnectAsync(cancellationToken);
            await adapter.SubscribeAsync(new[] { "BTCUSDT", "ETHUSDT" }, cancellationToken);
        }

        _logger.LogInformation("Feed handler started");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var adapter in _adapters)
        {
            await adapter.DisconnectAsync(cancellationToken);
        }

        _logger.LogInformation("Feed handler stopped");
    }

    public void AddAdapter(IFeedAdapter adapter)
    {
        _adapters.Add(adapter);
        adapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;
        adapter.Error += OnAdapterError;
        adapter.MarketDataReceived += OnMarketDataReceived;
    }

    public void RemoveAdapter(IFeedAdapter adapter)
    {
        _adapters.Remove(adapter);
        adapter.ConnectionStateChanged -= OnAdapterConnectionStateChanged;
        adapter.Error -= OnAdapterError;
        adapter.MarketDataReceived -= OnMarketDataReceived;
    }

    private void OnAdapterConnectionStateChanged(object? sender, OpenHFT.Feed.Interfaces.ConnectionStateChangedEventArgs e)
    {
        _logger.LogInformation("Adapter connection state changed: {IsConnected} - {Reason}", e.IsConnected, e.Reason);
    }

    private void OnAdapterError(object? sender, OpenHFT.Feed.Interfaces.FeedErrorEventArgs e)
    {
        _logger.LogError(e.Exception, "Adapter error: {Context}", e.Context);
    }

    private void OnMarketDataReceived(object? sender, OpenHFT.Core.Models.MarketDataEvent marketDataEvent)
    {
        // Forward market data to the queue for processing
        if (_marketDataWriter != null)
        {
            if (!_marketDataWriter.TryWrite(marketDataEvent))
            {
                _logger.LogWarning("Market data queue is full, dropping event for symbol {SymbolId}", marketDataEvent.SymbolId);
            }
        }
    }

    public void Dispose()
    {
        foreach (var adapter in _adapters)
        {
            adapter.Dispose();
        }
    }

    public void Initialize(LockFreeRingBuffer<MarketDataEvent> marketDataQueue)
    {
        throw new NotImplementedException();
    }
}
