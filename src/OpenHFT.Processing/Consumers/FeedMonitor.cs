using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Exceptions;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;
using OpenHFT.Processing;

namespace OpenHFT.Feed;

public record FeedAlert(ExchangeEnum SourceExchange, AlertLevel Level, string Message);
public record DepthSequence(long ts, long seq);
public enum AlertLevel { Info, Warning, Error, Critical }

public class FeedMonitor : BaseMarketDataConsumer
{
    private readonly IFeedHandler _feedHandler;
    private readonly IInstrumentRepository _repository;
    private readonly MarketDataDistributor _distributor;
    private readonly SubscriptionConfig _config;
    private readonly ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, ConcurrentDictionary<int, FeedStatistics>>> _statistics = new();
    private readonly ConcurrentDictionary<int, DepthSequence> _lastSequenceNumbers = new();

    public override string ConsumerName => "FeedMonitor";

    public override IReadOnlyCollection<Instrument> Instruments => Array.Empty<Instrument>();
    public event EventHandler<FeedAlert>? OnAlert;
    public FeedMonitor(IFeedHandler feedHandler, MarketDataDistributor distributor, ILogger<FeedMonitor> logger, SubscriptionConfig config, IInstrumentRepository repository) : base(logger, SystemTopic.FeedMonitor)
    {
        _feedHandler = feedHandler;
        _repository = repository;
        _distributor = distributor;
        _config = config;

        _feedHandler.AdapterConnectionStateChanged += OnConnectionStateChanged;
        _feedHandler.FeedError += OnFeedError;

        _distributor.Subscribe(this);
        _logger.LogInformationWithCaller("Feed Monitor started and subscribed to FeedHandler events.");
    }

    protected override void OnMarketData(in MarketDataEvent data)
    {
        var instrument = _repository.GetById(data.InstrumentId);
        if (instrument == null)
        {
            return;
        }

        var exchange = instrument.SourceExchange;
        var productType = instrument.ProductType;

        if (data.TopicId == 0) return; // Ignore events without a topic ID

        // 1. create stat object
        var productDict = _statistics.GetOrAdd(exchange, _ => new ConcurrentDictionary<ProductType, ConcurrentDictionary<int, FeedStatistics>>());
        var topicDict = productDict.GetOrAdd(productType, _ => new ConcurrentDictionary<int, FeedStatistics>());
        var stats = topicDict.GetOrAdd(data.TopicId, _ => new FeedStatistics());

        // 2. updates stat
        stats.RecordMessageReceived();
        stats.RecordMessageProcessed();

        // 3. validate sequence (only for DepthUpdate topic)
        if (TopicRegistry.TryGetTopic(data.TopicId, out var topic) && topic == BinanceTopic.DepthUpdate)
        {
            if (data.Sequence > 0 && data.PrevSequence > 0)
            {
                if (!_lastSequenceNumbers.TryGetValue(data.InstrumentId, out var lastSequence))
                {
                    lastSequence = new DepthSequence(data.Timestamp, data.Sequence);
                    _lastSequenceNumbers[data.InstrumentId] = lastSequence;
                }
                else
                {
                    if (data.Timestamp != lastSequence.ts)
                    {
                        if (data.PrevSequence != lastSequence.seq)
                        {
                            _logger.LogWarningWithCaller($"Sequence gap detected for {exchange}/{productType}/{instrument.Symbol}: expected {lastSequence.seq}, received {data.PrevSequence}");
                            stats.RecordSequenceGap();
                            OnAlert?.Invoke(this, new FeedAlert(exchange, AlertLevel.Error,
                                $"Sequence gap detected for {exchange}/{productType}/{instrument.Symbol}. Expected {lastSequence.seq}, received {data.PrevSequence}."));
                        }

                        _lastSequenceNumbers[data.InstrumentId] = new DepthSequence(data.Timestamp, data.Sequence);
                    }
                }
            }
        }

        // 3. latency calculation
        // Calculate latency in milliseconds. data.Timestamp is in milliseconds from the exchange.
        // GetSyncedTimestampMicros provides a local timestamp adjusted by the server time offset.
        var latency = (TimeSync.GetSyncedTimestampMicros(data.SourceExchange) / 1000.0) - data.Timestamp;
        if (latency > 0)
        {
            stats.AddE2ELatency(latency); // latency is already in milliseconds
        }

        // 4. alaert emergency
        if (stats.AvgE2ELatency > 1000)
        {
            OnAlert?.Invoke(this, new FeedAlert(exchange, AlertLevel.Warning,
                $"High E2E latency detected for {exchange}/{productType}: {stats.AvgE2ELatency:F2}ms"));
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        var adapter = sender as BaseFeedAdapter;
        if (adapter == null) return;

        var exchange = adapter.SourceExchange;
        var productType = adapter.ProdType;

        if (e.IsConnected)
        {
            // Increment reconnect count for all topics under this adapter's (Exchange, ProductType)
            if (_statistics.TryGetValue(exchange, out var productDict) &&
                productDict.TryGetValue(productType, out var topicDict))
            {
                foreach (var stat in topicDict.Values)
                {
                    stat.RecordReconnect();
                }
            }
            OnAlert?.Invoke(this, new FeedAlert(exchange, AlertLevel.Info,
               $"Adapter connected successfully: {exchange}/{productType}"));
        }
        else
        {
            OnAlert?.Invoke(this, new FeedAlert(e.SourceExchange, AlertLevel.Error,
               $"Adapter disconnected ({productType}). Reason: {e.Reason}"));
        }
    }

    private void OnFeedError(object? sender, FeedErrorEventArgs e)
    {
        var adapter = sender as BaseFeedAdapter;
        if (adapter == null) return;

        var exchange = adapter.SourceExchange;
        var productType = adapter.ProdType;

        // This is a generic error. We can't attribute it to a specific topic.
        // For now, we'll log it. A more sophisticated error reporting might be needed.
        _logger.LogErrorWithCaller(e.Exception, $"Feed error received for {exchange}/{productType}. Context: {e.Context}");

        var feedParseException = e.Exception as FeedParseException;
        if (feedParseException == null) return;

        if (_statistics.TryGetValue(exchange, out var productDict)
        && productDict.TryGetValue(productType, out var topicDict)
        && topicDict.TryGetValue(feedParseException.TopicId, out var stats))
        {
            stats.RecordMessageDropped();
        }
    }
}
