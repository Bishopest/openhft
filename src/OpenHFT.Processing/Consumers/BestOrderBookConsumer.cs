using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Models;

namespace OpenHFT.Processing.Consumers;

public class BestOrderBookConsumer : BaseMarketDataConsumer
{
    private readonly BestOrderBook _bestOrderBook;

    public readonly Instrument _instrument;

    public BestOrderBook BestBook => _bestOrderBook;

    public override string ConsumerName => $"BestOrderBookConsumer-{_instrument.Symbol}-{_instrument.ProductType}-{_instrument.SourceExchange}";

    public override IReadOnlyCollection<Instrument> Instruments => new List<Instrument> { _instrument };

    public override ExchangeTopic Topic => BinanceTopic.BookTicker;

    private readonly ConcurrentDictionary<string, EventHandler<BestOrderBook>> _subscribers = new();

    public BestOrderBookConsumer(Instrument instrument, ILogger logger) : base(logger)
    {
        _instrument = instrument;
        _bestOrderBook = new BestOrderBook(instrument);
    }

    public void AddSubscriber(string subscriberName, EventHandler<BestOrderBook> callback)
    {
        if (_subscribers.TryAdd(subscriberName, callback))
        {
            _logger.LogInformationWithCaller($"Subscriber '{subscriberName}' successfully added to BestOrderBookConsumer for {_instrument.Symbol}.");
        }
        else
        {
            _logger.LogWarningWithCaller($"Subscriber with name '{subscriberName}' already exists for {_instrument.Symbol}. Overwriting is not supported.");
        }
    }

    public void RemoveSubscriber(string subscriberName)
    {
        if (_subscribers.TryRemove(subscriberName, out _))
        {
            _logger.LogInformationWithCaller($"Subscriber '{subscriberName}' successfully removed from BestOrderBookConsumer for {_instrument.Symbol}.");
        }
        else
        {
            _logger.LogWarningWithCaller($"Subscriber with name '{subscriberName}' not found for {_instrument.Symbol}.");
        }
    }
    protected override void OnMarketData(in MarketDataEvent data)
    {
        _bestOrderBook.ApplyEvent(data);
        if (!_subscribers.IsEmpty)
        {
            foreach (var subscriber in _subscribers.Values)
            {
                try
                {
                    subscriber.Invoke(this, _bestOrderBook);
                }
                catch (Exception ex)
                {
                    _logger.LogErrorWithCaller(ex, $"An error occurred in a subscriber callback for {_instrument.Symbol}.");
                }
            }
        }
    }
}
