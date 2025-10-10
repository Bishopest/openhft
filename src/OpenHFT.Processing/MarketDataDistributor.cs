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
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, IMarketDataConsumer>> _subscriptions = new();
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
        foreach (var instrument in consumer.Instruments)
        {
            var innerSubscriptionDict = _subscriptions.GetOrAdd(
                instrument.InstrumentId,
                _ => new ConcurrentDictionary<int, IMarketDataConsumer>()
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
            if (TopicRegistry.TryGetTopic(eventCopied.TopicId, out var topic))
            {
                _logger.LogInformationWithCaller($"[{topic.EventTypeString}] {eventCopied}");
            }

            if (_subscriptions.TryGetValue(eventCopied.InstrumentId, out var subscribers) && subscribers.TryGetValue(eventCopied.TopicId, out var consumer))
            {
                consumer.Post(eventCopied);
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error distributing market data event sequence {sequence}");
        }
    }
}
