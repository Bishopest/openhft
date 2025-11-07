using System;
using System.Collections.Concurrent;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;

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

    public ExchangeFeedManager(ILogger<ExchangeFeedManager> logger, IInstrumentRepository instrumentRepository, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _instrumentRepository = instrumentRepository;
        _serviceProvider = serviceProvider;
        _feedAdapterConfigs = configuration.GetSection("feed").Get<List<FeedAdapterConfig>>() ?? new List<FeedAdapterConfig>();
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
            await adapter.SubscribeAsync(new List<Instrument> { instrument });
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
        await adapter.UnsubscribeAsync(new List<Instrument> { instrument });
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
                    config.ExecutionMode
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

            _logger.LogInformationWithCaller($"Connecting to {exchange}/{productType} adapter...");
            // Return the connection task itself, so it can be awaited.
            return newAdapter.ConnectAsync();
        });
    }

    private void HandleAdapterMarketData(object? sender, MarketDataEvent marketDataEvent)
    {
        OnMarketDataReceived?.Invoke(sender, marketDataEvent);
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
