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

    public async Task SubscribeToInstrumentAsync(int instrumentId)
    {
        var instrument = _instrumentRepository.GetById(instrumentId);
        if (instrument is null) return;

        var adapterKey = (instrument.SourceExchange, instrument.ProductType);

        // 1. Get or create the connection task. This will also create the adapter if it's the first time.
        var connectionTask = GetOrCreateConnectionTask(adapterKey);

        // 2. Await the connection task. This ensures the adapter is connected before proceeding.
        await connectionTask;

        if (_activeAdapters.TryGetValue(adapterKey, out var adapter) && adapter.IsConnected)
        {
            var topics = GetOrderBookTopicsForExchange(instrument.SourceExchange);
            await adapter.SubscribeAsync(new List<Instrument> { instrument }, topics);
        }
        else
        {
            _logger.LogWarningWithCaller($"Adapter for {adapterKey.Item1}/{adapterKey.Item2} failed to connect. Cannot subscribe.");
        }
    }

    public async Task UnsubscribeFromInstrumentAsync(int instrumentId)
    {
        var instrument = _instrumentRepository.GetById(instrumentId);
        if (instrument is null || !_activeAdapters.TryGetValue((instrument.SourceExchange, instrument.ProductType), out var adapter))
            return;

        if (!adapter.IsConnected)
        {
            _logger.LogWarningWithCaller($"Adapter for {instrument.SourceExchange}/{instrument.ProductType} is not connected. Cannot unsubscribe.");
            return;
        }
        var topics = GetOrderBookTopicsForExchange(instrument.SourceExchange);
        await adapter.UnsubscribeAsync(new List<Instrument> { instrument }, topics);
    }

    private Task GetOrCreateConnectionTask((ExchangeEnum, ProductType) adapterKey)
    {
        return _connectionTasks.GetOrAdd(adapterKey, key =>
        {
            var (exchange, productType) = key;
            var config = _feedAdapterConfigs.FirstOrDefault(c => c.Exchange == exchange && c.ProductType == productType);
            if (config is null)
            {
                _logger.LogWarningWithCaller($"No configuration for {exchange}/{productType}. Cannot create adapter.");
                return Task.CompletedTask; // Return a completed (failed) task
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
                _ => throw new NotSupportedException($"Adapter for {exchange} not supported.")
            };

            // Store the adapter instance immediately
            _activeAdapters[key] = newAdapter;
            newAdapter.MarketDataReceived += (sender, e) => OnMarketDataReceived?.Invoke(sender, e);
            newAdapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;

            _logger.LogInformationWithCaller($"Connecting to {exchange}/{productType} adapter...");
            // Return the connection task itself, so it can be awaited.
            return newAdapter.ConnectAsync();
        });
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
