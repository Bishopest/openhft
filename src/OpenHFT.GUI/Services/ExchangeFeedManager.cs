using System;
using System.Collections.Concurrent;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;
using OpenHFT.Gateway.ApiClient;

namespace OpenHFT.GUI.Services;

public class ExchangeFeedManager : IExchangeFeedManager, IAsyncDisposable
{
    private readonly ILogger<ExchangeFeedManager> _logger;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<(ExchangeEnum, ProductType), IFeedAdapter> _activeAdapters = new();
    private readonly ConcurrentDictionary<(ExchangeEnum, ProductType), Task> _connectionTasks = new();
    private readonly List<FeedAdapterConfig> _feedAdapterConfigs = new();
    public event EventHandler<MarketDataEvent>? OnMarketDataReceived;
    public event EventHandler<ConnectionStateChangedEventArgs>? AdapterConnectionStateChanged;

    public ExchangeFeedManager(
        ILogger<ExchangeFeedManager> logger,
        IInstrumentRepository instrumentRepository,
        IServiceProvider serviceProvider,
        List<FeedAdapterConfig> feedAdapterConfigs)
    {
        _logger = logger;
        _instrumentRepository = instrumentRepository;
        _serviceProvider = serviceProvider;
        _feedAdapterConfigs = feedAdapterConfigs;
    }

    public async Task SubscribeToInstrumentsAsync(IEnumerable<int> instrumentIds)
    {
        var instruments = instrumentIds
            .Select(id => _instrumentRepository.GetById(id))
            .Where(inst => inst != null)
            .ToList();

        if (!instruments.Any()) return;

        var instrumentsByAdapterKey = instruments
            .GroupBy(inst => (inst!.SourceExchange, inst.ProductType));

        var subscriptionTasks = new List<Task>();

        foreach (var group in instrumentsByAdapterKey)
        {
            var adapterKey = group.Key;
            var instrumentsForAdapter = group.ToList();

            Func<Task> subscribeAction = async () =>
            {
                var adapter = await GetOrCreateConnectionTask(adapterKey);
                if (adapter != null && adapter.IsConnected)
                {
                    var topics = GetOrderBookTopicsForExchange(adapterKey.Item1);
                    _logger.LogInformationWithCaller($"Subscribing to {instrumentsForAdapter.Count} instruments on {adapterKey}");
                    await adapter.SubscribeAsync(instrumentsForAdapter, topics);
                }
                else
                {
                    _logger.LogWarningWithCaller($"Adapter for {adapterKey} not ready. Cannot subscribe.");
                }
            };

            subscriptionTasks.Add(subscribeAction());
        }

        await Task.WhenAll(subscriptionTasks);
    }

    public async Task UnsubscribeFromInstrumentsAsync(IEnumerable<int> instrumentIds)
    {
        var instruments = instrumentIds
            .Select(id => _instrumentRepository.GetById(id))
            .Where(inst => inst != null)
            .ToList();

        if (!instruments.Any()) return;

        var instrumentsByAdapterKey = instruments
            .GroupBy(inst => (inst!.SourceExchange, inst.ProductType));

        var unsubscriptionTasks = new List<Task>();

        foreach (var group in instrumentsByAdapterKey)
        {
            var adapterKey = group.Key;
            var instrumentsForAdapter = group.ToList();

            if (_activeAdapters.TryGetValue(adapterKey, out var adapter) && adapter.IsConnected)
            {
                var topics = GetOrderBookTopicsForExchange(adapterKey.Item1);
                unsubscriptionTasks.Add(adapter.UnsubscribeAsync(instrumentsForAdapter, topics));
            }
        }

        await Task.WhenAll(unsubscriptionTasks);
    }

    private async Task<IFeedAdapter?> GetOrCreateConnectionTask((ExchangeEnum, ProductType) adapterKey)
    {
        if (_activeAdapters.TryGetValue(adapterKey, out var existingAdapter))
        {
            return existingAdapter;
        }

        var (exchange, productType) = adapterKey;
        var config = _feedAdapterConfigs.FirstOrDefault(c => c.Exchange == exchange && c.ProductType == productType);
        if (config is null)
        {
            _logger.LogWarningWithCaller($"No configuration for {exchange}/{productType}. Cannot create adapter.");
            return null;
        }

        IFeedAdapter newAdapter = exchange switch
        {
            ExchangeEnum.BINANCE => new BinanceAdapter(
                _serviceProvider.GetRequiredService<ILogger<BinanceAdapter>>(),
                productType,
                _instrumentRepository,
                config.ExecutionMode,
                new BinanceRestApiClient(
                    _serviceProvider.GetRequiredService<ILogger<BinanceRestApiClient>>(),
                    _instrumentRepository,
                    _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BinanceRestApiClient)),
                    productType,
                    config.ExecutionMode)
            ),
            ExchangeEnum.BITMEX => new BitmexAdapter(
                _serviceProvider.GetRequiredService<ILogger<BitmexAdapter>>(),
                productType,
                _instrumentRepository,
                config.ExecutionMode
            ),
            ExchangeEnum.BITHUMB => new BithumbPublicAdapter(
                _serviceProvider.GetRequiredService<ILogger<BithumbPublicAdapter>>(),
                productType,
                _instrumentRepository,
                config.ExecutionMode
            ),
            _ => throw new NotSupportedException($"Adapter for {exchange} not supported.")
        };

        if (_activeAdapters.TryAdd(adapterKey, newAdapter))
        {
            newAdapter.MarketDataReceived += HandleAdapterMarketData;
            newAdapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;

            _logger.LogInformationWithCaller($"Connecting to {exchange}/{productType} adapter...");
            await newAdapter.ConnectAsync();
            return newAdapter;
        }

        return _activeAdapters[adapterKey];
    }

    private void HandleAdapterMarketData(object? sender, MarketDataEvent marketDataEvent)
    {
        OnMarketDataReceived?.Invoke(sender, marketDataEvent);
    }

    /// <summary>
    /// A helper method to get the default market data topics for a given exchange.
    /// </summary>
    private IEnumerable<ExchangeTopic> GetOrderBookTopicsForExchange(ExchangeEnum exchange)
    {
        return exchange switch
        {
            ExchangeEnum.BINANCE => new List<BinanceTopic> { BinanceTopic.DepthUpdate },
            ExchangeEnum.BITMEX => new List<BitmexTopic> { BitmexTopic.OrderBookL2_25 },
            ExchangeEnum.BITHUMB => new List<BithumbTopic> { BithumbTopic.OrderBook },
            // Add other exchanges here
            _ => Enumerable.Empty<ExchangeTopic>()
        };
    }
    private void OnAdapterConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        var adapter = sender as IFeedAdapter;
        if (adapter == null) return;

        AdapterConnectionStateChanged?.Invoke(adapter, e);

        _logger.LogInformationWithCaller($"Adapter({adapter?.SourceExchange}) connection state changed: {e.IsConnected} - {e.Reason}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var adapter in _activeAdapters.Values)
        {
            adapter.MarketDataReceived -= HandleAdapterMarketData;
            await adapter.DisconnectAsync();
            adapter.Dispose();
        }
        _activeAdapters.Clear();
    }
}
