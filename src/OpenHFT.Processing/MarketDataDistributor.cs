using OpenHFT.Core.Models;
using OpenHFT.Processing.Interfaces;
using Microsoft.Extensions.Logging;
using Disruptor;
using OpenHFT.Core.Utils;
using Disruptor.Dsl;
using System.Collections.Concurrent;

namespace OpenHFT.Processing;

public readonly record struct ExchangeSymbolKey(ExchangeEnum Exchange, int SymbolId);

public class MarketDataDistributor : IEventHandler<MarketDataEventWrapper>
{
    private readonly Disruptor<MarketDataEventWrapper> _disruptor;
    private readonly ILogger<MarketDataDistributor> _logger;
    private readonly ConcurrentDictionary<ExchangeSymbolKey, ConcurrentDictionary<string, IMarketDataConsumer>> _subscriptions = new();
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
    public void Subscribe(IMarketDataConsumer consumer)
    {
        var key = new ExchangeSymbolKey(consumer.Exchange, consumer.SymbolId);

        var innerSubscriptionDict = _subscriptions.GetOrAdd(
            key,                                // 키
            _ => new ConcurrentDictionary<string, IMarketDataConsumer>()
        );

        if (!innerSubscriptionDict.TryAdd(consumer.ConsumerName, consumer))
        {
            _logger.LogWarningWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol id: {consumer.SymbolId}) already exists");
        }
        else
        {
            _logger.LogInformationWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol id: {consumer.SymbolId}) subscribed successfully.");
        }
    }

    public void Unsubscribe(IMarketDataConsumer consumer)
    {
        var key = new ExchangeSymbolKey(consumer.Exchange, consumer.SymbolId);

        if (_subscriptions.TryGetValue(key, out var innerSubscriptionDict))
        {
            if (innerSubscriptionDict.TryRemove(consumer.ConsumerName, out var removedConsumer))
            {
                _logger.LogInformationWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol id: {consumer.SymbolId}) unsubscribed successfully.");
            }
            else
            {
                _logger.LogWarningWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol id: {consumer.SymbolId}) not found for unsubscription.");
            }
        }
        else
        {
            _logger.LogWarningWithCaller($"Subscription key {key} not found for unsubscription.");
        }
    }

    public void OnEvent(MarketDataEventWrapper data, long sequence, bool endOfBatch)
    {
        try
        {
            Interlocked.Increment(ref _distributedEventCount);
            var marketEvent_copy = data.Event;
            var key = new ExchangeSymbolKey(marketEvent_copy.SourceExchange, marketEvent_copy.InstrumentId);
            if (_subscriptions.TryGetValue(key, out var subscribers))
            {
                foreach (var kvp in subscribers)
                {
                    var consumer = kvp.Value;
                    // Fire-and-forget
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            consumer.OnMarketData(marketEvent_copy);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogErrorWithCaller(ex, $"Error during consumer({consumer.ConsumerName} OnMarketData callback)");
                        }
                    });
                }
            }
            else
            {
                _logger.LogWarningWithCaller($"Subscription key {key} not found.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error distributing market data event sequence {sequence}");
        }
    }
}
