using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed;

public record FeedAlert(ExchangeEnum SourceExchange, AlertLevel Level, string Message);
public enum AlertLevel { Info, Warning, Error, Critical }

public class FeedMonitor
{
    private readonly IFeedHandler _feedHandler;
    private readonly ILogger<FeedMonitor> _logger;
    private readonly ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, FeedStatistics>> _statistics = new();
    private readonly ConcurrentDictionary<(ExchangeEnum Exchange, ProductType Type, int InstrumentId), long> _lastSequenceNumbers = new();
    public event Action<FeedAlert>? OnAlert;
    public FeedMonitor(IFeedHandler feedHandler, ILogger<FeedMonitor> logger)
    {
        _feedHandler = feedHandler;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _feedHandler.MarketDataReceived += OnMarketData;
        _feedHandler.AdapterConnectionStateChanged += OnConnectionStateChanged;
        _logger.LogInformationWithCaller("Feed Monitor started and subscribed to FeedHandler events.");
        return Task.CompletedTask;
    }

    private void OnMarketData(object? sender, MarketDataEvent e)
    {
        var adapter = sender as BaseFeedAdapter;
        if (adapter == null) return;

        var exchange = adapter.SourceExchange;
        var productType = adapter.ProductType;

        // 1. create stat object
        var innerDict = _statistics.GetOrAdd(exchange,
        _ => new ConcurrentDictionary<ProductType, FeedStatistics>());
        var stats = innerDict.GetOrAdd(productType,
        _ => new FeedStatistics());

        // 2. updates stat
        stats.RecordMessageProcessed();

        // 3. validate sequence
        if (e.Sequence > 0 && exchange == ExchangeEnum.BINANCE)
        {
            var key = (exchange, productType, e.InstrumentId);
            var currentSequence = e.Sequence;

            if (_lastSequenceNumbers.TryGetValue(key, out var lastSequence))
            {
                if (currentSequence != lastSequence + 1)
                {
                    _logger.LogWarningWithCaller($"Sequence gap detected for {exchange}/{productType}/{e.InstrumentId}: expected {lastSequence + 1}, received {currentSequence}");
                    stats.RecordSequenceGap();
                    OnAlert?.Invoke(new FeedAlert(exchange, AlertLevel.Error,
                        $"Sequence gap detected for {exchange}/{productType}/{e.InstrumentId}. Expected {lastSequence + 1}, received {currentSequence}."));
                }
            }

            _lastSequenceNumbers[key] = currentSequence;
        }

        // 3. latency calculation
        var latency = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.Timestamp) / 1000.0;
        if (latency > 0)
        {
            stats.AddE2ELatency(latency);
        }

        // 4. alaert emergency
        if (stats.AvgE2ELatency > 1000)
        {
            OnAlert?.Invoke(new FeedAlert(exchange, AlertLevel.Warning,
                $"High E2E latency detected for {exchange}/{productType}: {stats.AvgE2ELatency:F2}ms"));
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        var adapter = sender as BaseFeedAdapter;
        if (adapter == null) return;

        var exchange = adapter.SourceExchange;
        var productType = adapter.ProductType;

        var innerDict = _statistics.GetOrAdd(exchange,
            _ => new ConcurrentDictionary<ProductType, FeedStatistics>());

        var stats = innerDict.GetOrAdd(productType,
            _ => new FeedStatistics());

        if (e.IsConnected)
        {
            stats.RecordReconnect();
            OnAlert?.Invoke(new FeedAlert(exchange, AlertLevel.Info,
               $"Adapter connected successfully: {exchange}/{productType}"));
        }
        else
        {
            OnAlert?.Invoke(new FeedAlert(e.SourceExchange, AlertLevel.Error,
               $"Adapter disconnected ({productType}). Reason: {e.Reason}"));
        }
    }
}
