using System.Collections.Concurrent;
using Disruptor;
using Disruptor.Dsl;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Exceptions;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed;

public class FeedHandler : IFeedHandler
{
    private readonly ILogger<FeedHandler> _logger;
    private readonly ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, BaseFeedAdapter>> _adapters;
    private readonly RingBuffer<MarketDataEventWrapper> _ringBuffer;
    public event EventHandler<FeedErrorEventArgs>? FeedError;
    public event EventHandler<ConnectionStateChangedEventArgs>? AdapterConnectionStateChanged;

    public FeedHandlerStatistics Statistics { get; } = new();

    public FeedHandler(ILogger<FeedHandler> logger, ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, BaseFeedAdapter>> adapters, Disruptor<MarketDataEventWrapper> disruptor)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _ringBuffer = disruptor.RingBuffer ?? throw new ArgumentNullException(nameof(disruptor));

        Statistics.StartTime = DateTimeOffset.UtcNow;
        _logger.LogInformationWithCaller($"FeedHandler initialized with {_adapters.Count} adapters.");

        foreach (var exchangeKvp in adapters)
        {
            var exchangeName = exchangeKvp.Key;

            foreach (var adapterKvp in exchangeKvp.Value)
            {
                var adapter = adapterKvp.Value;

                adapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;
                adapter.Error += OnFeedError;
                adapter.MarketDataReceived += OnMarketDataReceived;
                _logger.LogInformationWithCaller($"Register market data handler for {exchangeName} adapter (Producer: {adapterKvp.Key})");
            }
        }
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        int cnt = 0;
        foreach (var exchangeKvp in _adapters)
        {
            foreach (var adapterKvp in exchangeKvp.Value)
            {
                await adapterKvp.Value.ConnectAsync(cancellationToken);
                cnt++;
            }
        }

        _logger.LogInformationWithCaller($"Feedhandler started with adapter({cnt})");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        int cnt = 0;
        foreach (var exchangeKvp in _adapters)
        {
            foreach (var adapterKvp in exchangeKvp.Value)
            {
                await adapterKvp.Value.DisconnectAsync(cancellationToken);
                cnt++;
            }
        }

        _logger.LogInformationWithCaller($"Feedhandler stopped with adapter({cnt})");
    }

    public void AddAdapter(BaseFeedAdapter adapter)
    {
        var innerDict = _adapters.GetOrAdd(
        adapter.SourceExchange,
        _ => new ConcurrentDictionary<ProductType, BaseFeedAdapter>()
    );

        // 2. 내부 딕셔너리에 ProducerType 키와 함께 어댑터를 추가합니다.
        if (innerDict.TryAdd(adapter.ProdType, adapter)) // assumes adapter has ProducerType property
        {
            adapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;
            adapter.Error += OnFeedError;
            adapter.MarketDataReceived += OnMarketDataReceived;
            _logger.LogInformationWithCaller($"Adapter for {Exchange.Decode(adapter.SourceExchange)} ({adapter.ProdType}) added Feedhandler");
        }
    }

    public void RemoveAdapter(ExchangeEnum sourceExchange, ProductType type)
    {
        if (_adapters.TryGetValue(sourceExchange, out var innerDict))
        {
            if (innerDict.TryRemove(type, out var adapter))
            {
                adapter.ConnectionStateChanged -= OnAdapterConnectionStateChanged;
                adapter.Error -= OnFeedError;
                adapter.MarketDataReceived -= OnMarketDataReceived;
                _logger.LogInformationWithCaller($"Adapter for {Exchange.Decode(adapter.SourceExchange)} ({type}) removed from Feedhandler");

            }
        }
    }
    /// <summary>
    /// Retrieves the specific BaseFeedAdapter for the given exchange and producer type.
    /// </summary>
    /// <param name="sourceExchange">The exchange enum of the desired adapter.</param>
    /// <param name="type">The producer type of the desired adapter.</param>
    /// <returns>The requested BaseFeedAdapter instance.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if the adapter for the specified exchange and producer type is not found.</exception>
    public BaseFeedAdapter? GetAdapter(ExchangeEnum sourceExchange, ProductType type)
    {
        // 1. Exchange 키로 내부 딕셔너리를 찾습니다.
        if (_adapters.TryGetValue(sourceExchange, out var innerDict))
        {
            // 2. ProducerType 키로 어댑터를 찾습니다.
            if (innerDict.TryGetValue(type, out var adapter))
            {
                return adapter;
            }
            else
            {
                var msg = $"Adapter for exchange {Exchange.Decode(sourceExchange)} and ProducerType {type} not found.";
                _logger.LogError(msg);
                throw new KeyNotFoundException(msg);
            }
        }
        else
        {
            // 3. Exchange 키 자체가 존재하지 않을 경우 예외를 발생시킵니다.
            var msg = $"Adapter for exchange {Exchange.Decode(sourceExchange)} not found in FeedHandler.";
            _logger.LogError(msg);
            throw new KeyNotFoundException(msg);
        }
    }

    private void OnAdapterConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        var adapter = sender as BaseFeedAdapter;
        if (adapter == null) return;

        AdapterConnectionStateChanged?.Invoke(adapter, e);

        _logger.LogInformationWithCaller($"Adapter({adapter?.SourceExchange}) connection state changed: {e.IsConnected} - {e.Reason}");

        if (!e.IsConnected)
        {
            _logger.LogInformationWithCaller($"Adapter({adapter?.SourceExchange}) reconnect attempt start");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                if (adapter != null)
                {
                    await adapter.ConnectAsync();
                }
            });
        }
    }

    private void OnFeedError(object? sender, FeedErrorEventArgs e)
    {
        _logger.LogErrorWithCaller(e.Exception, $"Adapter error: {e.Context}");
        FeedError?.Invoke(sender, e);
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
                _logger.LogWarningWithCaller($"Disruptor ring buffer is full. Dropping market data for SymbolId {marketDataEvent.InstrumentId}. This indicates the consumer is too slow or has stalled.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "An unexpected error occurred while publishing to the disruptor ring buffer.");
        }
    }

    public void Dispose()
    {
        foreach (var exchangeKvp in _adapters)
        {
            foreach (var adapterKvp in exchangeKvp.Value)
            {
                adapterKvp.Value.Dispose();
            }
        }
    }
}