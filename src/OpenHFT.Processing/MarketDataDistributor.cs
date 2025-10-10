using OpenHFT.Core.Models;
using OpenHFT.Processing.Interfaces;
using Microsoft.Extensions.Logging;
using Disruptor;
using OpenHFT.Core.Utils;
using Disruptor.Dsl;
using System.Collections.Concurrent;
using OpenHFT.Core.Interfaces;
using OpenHFT.Feed.Models;

namespace OpenHFT.Processing;

public class MarketDataDistributor : IEventHandler<MarketDataEventWrapper>
{
    private readonly Disruptor<MarketDataEventWrapper> _disruptor;
    private readonly ILogger<MarketDataDistributor> _logger;
    // key = InstrumentID, key inside = Topic id
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, BaseMarketDataConsumer>> _subscriptions = new();
    // key = TopicId, value = Consumer. For system-wide topics not tied to a specific instrument.
    private readonly ConcurrentDictionary<int, BaseMarketDataConsumer> _systemTopicSubscriptions = new();
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
    public void Subscribe(BaseMarketDataConsumer consumer)
    {
        // Handle system topics separately. They are not tied to specific instruments.
        if (consumer.Topic is SystemTopic)
        {
            if (_systemTopicSubscriptions.TryAdd(consumer.Topic.TopicId, consumer))
            {
                consumer.Start();
                _logger.LogInformationWithCaller($"System subscriber(name: {consumer.ConsumerName}, topic id: {consumer.Topic.TopicId}) subscribed and started successfully.");
            }
            else
            {
                _logger.LogWarningWithCaller($"System subscriber(name: {consumer.ConsumerName}, topic id: {consumer.Topic.TopicId}) already exists.");
            }
            return;
        }

        foreach (var instrument in consumer.Instruments)
        {
            var innerSubscriptionDict = _subscriptions.GetOrAdd(
                instrument.InstrumentId,
                _ => new ConcurrentDictionary<int, BaseMarketDataConsumer>()
            );

            if (!innerSubscriptionDict.TryAdd(consumer.Topic.TopicId, consumer))
            {
                _logger.LogWarningWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {instrument.SourceExchange}, symbol id: {instrument.InstrumentId}, topic id: {consumer.Topic.TopicId}) already exists");
            }
            else
            {
                consumer.Start();
                _logger.LogInformationWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {instrument.SourceExchange}, symbol id: {instrument.InstrumentId}, topic id: {consumer.Topic.TopicId})) subscribed and started successfully.");
            }
        }
    }

    public void Unsubscribe(BaseMarketDataConsumer consumer)
    {
        // Handle system topics
        if (consumer.Topic is SystemTopic)
        {
            if (_systemTopicSubscriptions.TryRemove(consumer.Topic.TopicId, out var removedConsumer))
            {
                removedConsumer.Stop();
                _logger.LogInformationWithCaller($"System subscriber(name: {consumer.ConsumerName}, topic id: {consumer.Topic.TopicId}) unsubscribed and stopped successfully.");
            }
            else
            {
                _logger.LogWarningWithCaller($"System subscriber(name: {consumer.ConsumerName}, topic id: {consumer.Topic.TopicId}) not found for unsubscription.");
            }
            return;
        }

        foreach (var instrument in consumer.Instruments)
        {
            if (_subscriptions.TryGetValue(instrument.InstrumentId, out var innerSubscriptionDict))
            {
                if (innerSubscriptionDict.TryRemove(consumer.Topic.TopicId, out var removedConsumer))
                {
                    removedConsumer.Stop();
                    _logger.LogInformationWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {instrument.SourceExchange}, symbol id: {instrument.InstrumentId}, topic id: {consumer.Topic.TopicId})) unsubscribed and stopped successfully.");
                }
                else
                {
                    _logger.LogWarningWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {instrument.SourceExchange}, symbol id: {instrument.InstrumentId}, topic id: {consumer.Topic.TopicId})) not found for unsubscription.");
                }
            }
            else
            {
                _logger.LogWarningWithCaller($"Subscription symbol id: {instrument.InstrumentId}) not found for unsubscription.");
            }

        }
    }

    public void OnEvent(MarketDataEventWrapper data, long sequence, bool endOfBatch)
    {
        try
        {
            //Interlocked.Increment(ref _distributedEventCount);
            var eventCopied = data.Event;
            // if (TopicRegistry.TryGetTopic(eventCopied.TopicId, out var topic))
            // {
            //     _logger.LogInformationWithCaller($"[{topic.EventTypeString}] {eventCopied}");
            // }

            // 1. Post to the specific consumer for the instrument and topic.
            if (_subscriptions.TryGetValue(eventCopied.InstrumentId, out var subscribers) && subscribers.TryGetValue(eventCopied.TopicId, out var consumer))
            {
                consumer.Post(eventCopied);
            }

            // 2. Also, post to all system topic consumers.
            // This allows consumers like FeedMonitor to see all events regardless of instrument.
            // The check `!_systemTopicSubscriptions.IsEmpty` is a quick optimization.
            if (!_systemTopicSubscriptions.IsEmpty)
            {
                foreach (var systemConsumer in _systemTopicSubscriptions.Values) systemConsumer.Post(eventCopied);
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error distributing market data event sequence {sequence}");
        }
    }
}
