using OpenHFT.Core.Models;
using OpenHFT.Processing.Interfaces;
using Microsoft.Extensions.Logging;
using Disruptor;
using OpenHFT.Core.Utils;
using Disruptor.Dsl;

namespace OpenHFT.Processing;

public class MarketDataDistributor : IEventHandler<MarketDataEventWrapper>
{
    private readonly Disruptor<MarketDataEventWrapper> _disruptor;
    private readonly ILogger<MarketDataDistributor> _logger;
    private readonly Dictionary<int, HashSet<IMarketDataConsumer>> _symbolSubscriptions = new();
    private long _distributedEventCount;

    public MarketDataDistributor(Disruptor<MarketDataEventWrapper> disruptor, ILogger<MarketDataDistributor> logger)
    {
        _disruptor = disruptor;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Market Data Distributor is starting.");

        _disruptor.HandleEventsWith(this);
        _disruptor.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Market Data Distributor is stopping.");
        _disruptor.Shutdown(TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    // Subscription management
    public void Subscribe(IMarketDataConsumer consumer, int symbolId)
    {
        if (!_symbolSubscriptions.ContainsKey(symbolId))
        {
            _symbolSubscriptions[symbolId] = new HashSet<IMarketDataConsumer>();
        }

        _symbolSubscriptions[symbolId].Add(consumer);
        _logger.LogInformation("Consumer {Consumer} subscribed to symbol {SymbolId}",
            consumer.GetType().Name, symbolId);
    }

    public void Unsubscribe(IMarketDataConsumer consumer, int symbolId)
    {
        if (_symbolSubscriptions.TryGetValue(symbolId, out var subscribers))
        {
            subscribers.Remove(consumer);

            if (subscribers.Count == 0)
            {
                _symbolSubscriptions.Remove(symbolId);
            }
        }
    }

    public void OnEvent(MarketDataEventWrapper data, long sequence, bool endOfBatch)
    {
        try
        {
            Interlocked.Increment(ref _distributedEventCount);
            var marketEvent_copy = data.Event;

            if (_symbolSubscriptions.TryGetValue(marketEvent_copy.SymbolId, out var subscribers))
            {
                foreach (var subscriber in subscribers)
                {
                    var consumer = subscriber;
                    // Fire-and-forget
                    _ = Task.Run(() =>
                    {
                        try { consumer.OnMarketData(marketEvent_copy); }
                        catch (Exception ex) { /* ... 에러 로깅 ... */ }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error distributing market data event sequence {sequence}");
        }
    }
}
