using System.Collections.Concurrent;
using Disruptor;
using Disruptor.Dsl;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed;

public class FeedHandler : IFeedHandler
{
    private readonly ILogger<FeedHandler> _logger;
    private readonly IEnumerable<IFeedAdapter> _adapters;
    private readonly RingBuffer<MarketDataEventWrapper> _ringBuffer;
    private readonly RingBuffer<OrderStatusReportWrapper> _orderUpdateRingBuffer;
    public event EventHandler<FeedErrorEventArgs>? FeedError;
    public event EventHandler<ConnectionStateChangedEventArgs>? AdapterConnectionStateChanged;
    public event EventHandler<AuthenticationEventArgs>? AdapterAuthenticationStateChanged;

    public FeedHandlerStatistics Statistics { get; } = new();

    public FeedHandler(
        ILogger<FeedHandler> logger,
        IFeedAdapterRegistry adapterRegistry,
        Disruptor<MarketDataEventWrapper> disruptor,
        Disruptor<OrderStatusReportWrapper> orderUpdateDisruptor)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _adapters = adapterRegistry.GetAllAdapters() ?? throw new ArgumentNullException(nameof(adapterRegistry));
        _ringBuffer = disruptor.RingBuffer ?? throw new ArgumentNullException(nameof(disruptor));
        _orderUpdateRingBuffer = orderUpdateDisruptor.RingBuffer ?? throw new ArgumentNullException(nameof(orderUpdateDisruptor));

        Statistics.StartTime = DateTimeOffset.UtcNow;
        _logger.LogInformationWithCaller($"FeedHandler initialized with {_adapters.Count()} adapters.");

        foreach (var adapter in _adapters)
        {
            adapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;
            adapter.Error += OnFeedError;
            adapter.MarketDataReceived += OnMarketDataReceived;
            adapter.OrderUpdateReceived += OnOrderUpdateReceived;

            if (adapter is BaseAuthFeedAdapter authAdapter)
            {
                authAdapter.AuthenticationStateChanged += onAdapterAuthenticationStateChanged;
            }
            _logger.LogInformationWithCaller($"Register market data handler for {adapter.SourceExchange} adapter (Product Type: {adapter.ProdType}");
        }
    }

    private void onAdapterAuthenticationStateChanged(object? sender, AuthenticationEventArgs e)
    {
        var adapter = sender as IFeedAdapter;
        if (adapter == null) return;

        AdapterAuthenticationStateChanged?.Invoke(adapter, e);
    }

    private void OnAdapterConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        var adapter = sender as IFeedAdapter;
        if (adapter == null) return;

        AdapterConnectionStateChanged?.Invoke(adapter, e);

        _logger.LogInformationWithCaller($"Adapter({adapter?.SourceExchange}) connection state changed: {e.IsConnected} - {e.Reason}");
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

    private void OnOrderUpdateReceived(object? sender, OrderStatusReport orderStatusReport)
    {
        if (_orderUpdateRingBuffer.TryNext(out long sequence))
        {
            try
            {
                var wrapper = _orderUpdateRingBuffer[sequence];
                wrapper.SetData(orderStatusReport);
            }
            finally
            {
                _orderUpdateRingBuffer.Publish(sequence);
            }
        }
        else
        {
            _logger.LogWarningWithCaller($"Order Disruptor ring buffer is full. Dropping order update for OrderId {orderStatusReport.ClientOrderId}. This indicates the consumer is too slow or has stalled.");
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