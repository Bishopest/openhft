using System.Collections.Concurrent;
using Disruptor;
using Disruptor.Dsl;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed;

public class FeedHandler : IFeedHandler
{
    private readonly ILogger<FeedHandler> _logger;
    private readonly ConcurrentDictionary<ExchangeEnum, IFeedAdapter> _adapters;
    private readonly RingBuffer<MarketDataEventWrapper> _ringBuffer;

    public event EventHandler<MarketDataEvent> MarketDataReceived;
    public event EventHandler<ConnectionStateChangedEventArgs> AdapterConnectionStateChanged;

    public FeedHandlerStatistics Statistics { get; } = new();

    public FeedHandler(ILogger<FeedHandler> logger, ConcurrentDictionary<ExchangeEnum, IFeedAdapter> adapters, Disruptor<MarketDataEventWrapper> disruptor)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _ringBuffer = disruptor.RingBuffer ?? throw new ArgumentNullException(nameof(disruptor));

        Statistics.StartTime = DateTimeOffset.UtcNow;
        _logger.LogInformationWithCaller($"FeedHandler initialized with {_adapters.Count} adapters.");

        foreach (var kvp in adapters)
        {
            var exchangeName = kvp.Key;
            var adapter = kvp.Value;

            adapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;
            adapter.Error += OnFeedError;
            adapter.MarketDataReceived += OnMarketDataReceived;
            _logger.LogInformationWithCaller($"Register market data handler for {exchangeName} adapter");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        int cnt = 0;
        foreach (var kvp in _adapters)
        {
            await kvp.Value.ConnectAsync(cancellationToken);
            await kvp.Value.SubscribeAsync(new[] { "BTCUSDT", "ETHUSDT" }, cancellationToken);
            cnt++;
        }

        _logger.LogInformationWithCaller($"Feedhandler started with adapter({cnt})");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        int cnt = 0;
        foreach (var kvp in _adapters)
        {
            await kvp.Value.DisconnectAsync(cancellationToken);
            cnt++;
        }

        _logger.LogInformationWithCaller($"Feedhandler stopped with adapter({cnt})");
    }

    public void AddAdapter(IFeedAdapter adapter)
    {
        if (_adapters.TryAdd(adapter.Exchange, adapter))
        {
            adapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;
            adapter.Error += OnFeedError;
            adapter.MarketDataReceived += OnMarketDataReceived;
            _logger.LogInformationWithCaller($"Adapter for {Exchange.Decode(adapter.Exchange)} added Feedhandler");
        }
    }

    public void RemoveAdapter(ExchangeEnum sourceExchange)
    {
        if (_adapters.Remove(sourceExchange, out var adapter))
        {
            adapter.ConnectionStateChanged -= OnAdapterConnectionStateChanged;
            adapter.Error -= OnFeedError;
            adapter.MarketDataReceived -= OnMarketDataReceived;
            _logger.LogInformationWithCaller($"Adapter for {Exchange.Decode(adapter.Exchange)} removed from Feedhandler");
        }
    }

    private void OnAdapterConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _logger.LogInformationWithCaller($"Adapter connection state changed: {e.IsConnected} - {e.Reason}");
    }

    private void OnFeedError(object? sender, FeedErrorEventArgs e)
    {
        _logger.LogErrorWithCaller(e.Exception, $"Adapter error: {e.Context}");
    }

    private void OnMarketDataReceived(object? sender, MarketDataEvent marketDataEvent)
    {
        try
        {
            if (_ringBuffer.TryNext(out long sequence))
            {
                try
                {
                    var wrapper = _ringBuffer[sequence];
                    wrapper.SetData(marketDataEvent);
                }
                finally
                {
                    _ringBuffer.Publish(sequence);
                }
            }
            else
            {
                _logger.LogWarningWithCaller($"Disruptor ring buffer is full. Dropping market data for SymbolId {marketDataEvent.SymbolId}. This indicates the consumer is too slow or has stalled.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "An unexpected error occurred while publishing to the disruptor ring buffer.");
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _adapters)
        {
            kvp.Value.Dispose();
        }
    }
}