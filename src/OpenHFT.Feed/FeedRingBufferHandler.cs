using Microsoft.Extensions.Logging;
using OpenHFT.Core.Collections;
using OpenHFT.Core.Models;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed;

public class FeedRingBufferHandler : IFeedHandler
{
    private readonly ILogger<FeedRingBufferHandler> _logger;
    private readonly List<IFeedAdapter> _adapters = new();
    private LockFreeRingBuffer<MarketDataEvent>? _marketDataQueue;

    public IReadOnlyList<IFeedAdapter> Adapters => _adapters.AsReadOnly();
    public FeedHandlerStatistics Statistics { get; } = new();

    public event EventHandler<MarketDataEvent>? MarketDataReceived;
    public event EventHandler<GapDetectedEventArgs>? GapDetected;

    public FeedRingBufferHandler(ILogger<FeedRingBufferHandler> logger, LockFreeRingBuffer<MarketDataEvent> marketDataQueue)
    {
        _logger = logger;
        _marketDataQueue = marketDataQueue;
        Statistics.StartTime = DateTimeOffset.UtcNow;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var adapter in _adapters)
        {
            await adapter.ConnectAsync(cancellationToken);
            await adapter.SubscribeAsync(new[] { "BTCUSDT", "ETHUSDT" }, cancellationToken);
            await adapter.StartAsync(cancellationToken);
        }

        _logger.LogInformation("Feed handler started");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var adapter in _adapters)
        {
            await adapter.StopAsync(cancellationToken);
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
        if (_marketDataQueue != null)
        {
            if (!_marketDataQueue.TryWrite(marketDataEvent))
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
}
