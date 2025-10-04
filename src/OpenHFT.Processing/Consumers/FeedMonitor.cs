using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Processing;
using OpenHFT.Processing.Interfaces;

namespace OpenHFT.Feed;

public record FeedAlert(ExchangeEnum SourceExchange, AlertLevel Level, string Message);
public enum AlertLevel { Info, Warning, Error, Critical }

public class FeedMonitor : BaseMarketDataConsumer
{
    private readonly IFeedHandler _feedHandler;
    private readonly IInstrumentRepository _repository;
    private readonly MarketDataDistributor _distributor;
    private readonly ILogger<FeedMonitor> _logger;
    private readonly SubscriptionConfig _config;
    private readonly ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, FeedStatistics>> _statistics = new();
    private readonly ConcurrentDictionary<(ExchangeEnum Exchange, ProductType Type, int InstrumentId), long> _lastSequenceNumbers = new();

    public override string ConsumerName => "FeedMonitor";

    private List<Instrument> _instruments = new List<Instrument>();
    public override IReadOnlyCollection<Instrument> Instruments => _instruments;

    public event EventHandler<FeedAlert>? OnAlert;
    public FeedMonitor(IFeedHandler feedHandler, MarketDataDistributor distributor, ILogger<FeedMonitor> logger, SubscriptionConfig config, IInstrumentRepository repository) : base(logger)
    {
        _feedHandler = feedHandler;
        _repository = repository;
        _distributor = distributor;
        _logger = logger;
        _config = config;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _feedHandler.AdapterConnectionStateChanged += OnConnectionStateChanged;

        foreach (var group in _config.Subscriptions)
        {
            if (!Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange) ||
                !Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
            {
                continue;
            }

            foreach (var symbol in group.Symbols)
            {
                var instrument = _repository.FindBySymbol(symbol, productType, exchange);
                if (instrument == null)
                {
                    _logger.LogWarningWithCaller($"Could not find symbol {symbol} prod-type {productType} on exchange {exchange}.");
                    continue;
                }

                _instruments.Add(instrument);
            }
        }
        _distributor.Subscribe(this);
        _logger.LogInformationWithCaller("Feed Monitor started and subscribed to FeedHandler events.");
        return Task.CompletedTask;
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

        // 1. create stat object
        var innerDict = _statistics.GetOrAdd(exchange,
        _ => new ConcurrentDictionary<ProductType, FeedStatistics>());
        var stats = innerDict.GetOrAdd(productType,
        _ => new FeedStatistics());

        // 2. updates stat
        stats.RecordMessageProcessed();

        // 3. validate sequence
        if (data.Sequence > 0 && exchange == ExchangeEnum.BINANCE)
        {
            var key = (exchange, productType, data.InstrumentId);
            var currentSequence = data.Sequence;

            if (_lastSequenceNumbers.TryGetValue(key, out var lastSequence))
            {
                if (currentSequence != lastSequence + 1)
                {
                    _logger.LogWarningWithCaller($"Sequence gap detected for {exchange}/{productType}/{data.InstrumentId}: expected {lastSequence + 1}, received {currentSequence}");
                    stats.RecordSequenceGap();
                    OnAlert?.Invoke(this, new FeedAlert(exchange, AlertLevel.Error,
                        $"Sequence gap detected for {exchange}/{productType}/{data.InstrumentId}. Expected {lastSequence + 1}, received {currentSequence}."));
                }
            }

            _lastSequenceNumbers[key] = currentSequence;
        }

        // 3. latency calculation
        var latency = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - data.Timestamp) / 1000.0;
        if (latency > 0)
        {
            stats.AddE2ELatency(latency);
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
        var productType = adapter.ProductType;

        var innerDict = _statistics.GetOrAdd(exchange,
            _ => new ConcurrentDictionary<ProductType, FeedStatistics>());

        var stats = innerDict.GetOrAdd(productType,
            _ => new FeedStatistics());

        if (e.IsConnected)
        {
            stats.RecordReconnect();
            OnAlert?.Invoke(this, new FeedAlert(exchange, AlertLevel.Info,
               $"Adapter connected successfully: {exchange}/{productType}"));
        }
        else
        {
            OnAlert?.Invoke(this, new FeedAlert(e.SourceExchange, AlertLevel.Error,
               $"Adapter disconnected ({productType}). Reason: {e.Reason}"));
        }
    }
}
