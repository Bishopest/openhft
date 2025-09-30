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
    private readonly IInstrumentRepository _instrumentRepository;
    // key = InstrumentID
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, IMarketDataConsumer>> _subscriptions = new();
    private long _distributedEventCount;

    public MarketDataDistributor(Disruptor<MarketDataEventWrapper> disruptor, ILogger<MarketDataDistributor> logger, IInstrumentRepository instrumentRepository)
    {
        _disruptor = disruptor;
        _instrumentRepository = instrumentRepository;
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
        var instrument = _instrumentRepository.GetById(consumer.InstrumentId);
        if (instrument == null) return;

        var innerSubscriptionDict = _subscriptions.GetOrAdd(
            consumer.InstrumentId,
            _ => new ConcurrentDictionary<string, IMarketDataConsumer>()
        );

        if (!innerSubscriptionDict.TryAdd(consumer.ConsumerName, consumer))
        {
            _logger.LogWarningWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol: {instrument.Symbol}) already exists");
        }
        else
        {
            _logger.LogInformationWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol: {instrument.Symbol}) subscribed successfully.");
        }
    }

    public void Unsubscribe(IMarketDataConsumer consumer)
    {
        var instrument = _instrumentRepository.GetById(consumer.InstrumentId);
        if (instrument == null) return;

        if (_subscriptions.TryGetValue(consumer.InstrumentId, out var innerSubscriptionDict))
        {
            if (innerSubscriptionDict.TryRemove(consumer.ConsumerName, out var removedConsumer))
            {
                _logger.LogInformationWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol: {instrument.Symbol}) unsubscribed successfully.");
            }
            else
            {
                _logger.LogWarningWithCaller($"Subscriber(name: {consumer.ConsumerName}, exchange: {consumer.Exchange}, symbol: {instrument.Symbol}) not found for unsubscription.");
            }
        }
        else
        {
            _logger.LogWarningWithCaller($"Subscription symbol: {instrument.Symbol}(id: {consumer.InstrumentId}) not found for unsubscription.");
        }
    }

    public void OnEvent(MarketDataEventWrapper data, long sequence, bool endOfBatch)
    {
        try
        {
            Interlocked.Increment(ref _distributedEventCount);
            var marketEvent_copy = data.Event;
            var instrument = _instrumentRepository.GetById(marketEvent_copy.InstrumentId);
            if (instrument == null) return;

            if (_subscriptions.TryGetValue(marketEvent_copy.InstrumentId, out var subscribers))
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
                _logger.LogWarningWithCaller($"Subscription symbol: {instrument.Symbol}(id: {instrument.InstrumentId}) not found.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error distributing market data event sequence {sequence}");
        }
    }
}
