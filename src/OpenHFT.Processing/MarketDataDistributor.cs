using OpenHFT.Core.Models;
using OpenHFT.Processing.Interfaces;
using Microsoft.Extensions.Logging;
using Disruptor;
using OpenHFT.Core.Utils;
using Disruptor.Dsl;
using System.Collections.Concurrent;
using OpenHFT.Core.Interfaces;

namespace OpenHFT.Processing;

public class MarketDataDistributor : IEventHandler<MarketDataEventWrapper>
{
    private readonly Disruptor<MarketDataEventWrapper> _disruptor;
    private readonly ILogger<MarketDataDistributor> _logger;
    // key = InstrumentID
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, IMarketDataConsumer>> _subscriptions = new();
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
        var innerSubscriptionDict = _subscriptions.GetOrAdd(
            consumer.InstrumentId,
            _ => new ConcurrentDictionary<string, IMarketDataConsumer>()
        );

        if (!innerSubscriptionDict.TryAdd(consumer.ConsumerName, consumer))
        {
            _logger.LogWarningWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol id: {consumer.InstrumentId}) already exists");
        }
        else
        {
            consumer.Start();
            _logger.LogInformationWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol id: {consumer.InstrumentId}) subscribed and started successfully.");
        }
    }

    public void Unsubscribe(IMarketDataConsumer consumer)
    {
        if (_subscriptions.TryGetValue(consumer.InstrumentId, out var innerSubscriptionDict))
        {
            if (innerSubscriptionDict.TryRemove(consumer.ConsumerName, out var removedConsumer))
            {
                removedConsumer.Stop();
                _logger.LogInformationWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol id: {consumer.InstrumentId}) unsubscribed and stopped successfully.");
            }
            else
            {
                _logger.LogWarningWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol id: {consumer.InstrumentId}) not found for unsubscription.");
            }
        }
        else
        {
            _logger.LogWarningWithCaller($"Subscription symbol id: {consumer.InstrumentId}) not found for unsubscription.");
        }
    }

    public void OnEvent(MarketDataEventWrapper data, long sequence, bool endOfBatch)
    {
        try
        {
            //Interlocked.Increment(ref _distributedEventCount);
            var eventCopied = data.Event;

            if (_subscriptions.TryGetValue(eventCopied.InstrumentId, out var subscribers))
            {
                foreach (var kvp in subscribers)
                {
                    kvp.Value.Post(eventCopied);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error distributing market data event sequence {sequence}");
        }
    }
}
