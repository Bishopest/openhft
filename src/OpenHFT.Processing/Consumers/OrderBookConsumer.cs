using System;
using System.Collections.Concurrent;
using Disruptor;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Models;

namespace OpenHFT.Processing.Consumers;

/// <summary>
/// A market data consumer that maintains an in-memory order book for a specific instrument.
/// </summary>
public class OrderBookConsumer : BaseMarketDataConsumer
{
    private readonly OrderBook _orderBook;
    private readonly Instrument _instrument;
    /// <summary>
    /// The underlying order book managed by this consumer.
    /// </summary>
    public OrderBook Book => _orderBook;

    public override string ConsumerName => $"OrderBookConsumer-{_instrument.Symbol}-{_instrument.ProductType}-{_instrument.SourceExchange}";

    public override IReadOnlyCollection<Instrument> Instruments => new List<Instrument> { _instrument };

    private readonly ConcurrentDictionary<string, EventHandler<OrderBook>> _subscribers = new();

    public OrderBookConsumer(Instrument instrument, ILogger logger, ExchangeTopic topic) : base(logger, topic)
    {
        _instrument = instrument;
        _orderBook = new OrderBook(instrument, null);
    }

    public void AddSubscriber(string subscriberName, EventHandler<OrderBook> callback)
    {
        if (_subscribers.TryAdd(subscriberName, callback))
        {
            _logger.LogInformationWithCaller($"Subscriber '{subscriberName}' successfully added to OrderBookConsumer for {_instrument.Symbol}.");
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
            _logger.LogInformationWithCaller($"Subscriber '{subscriberName}' successfully removed from OrderBookConsumer for {_instrument.Symbol}.");
        }
        else
        {
            _logger.LogWarningWithCaller($"Subscriber with name '{subscriberName}' not found for {_instrument.Symbol}.");
        }
    }

    /// <summary>
    /// Processes an incoming market data event by applying it to the order book.
    /// This method is called from the dedicated consumer processing thread.
    /// </summary>
    /// <param name="data">The market data event to process.</param>
    protected override void OnMarketData(in MarketDataEvent data)
    {
        // Apply the event to our internal order book.
        _orderBook.ApplyEvent(data);
        // Notify all subscribers (strategies) that the book has changed.
        if (!_subscribers.IsEmpty)
        {
            foreach (var subscriber in _subscribers.Values)
            {
                try
                {
                    // Invoke the callback. We pass 'this' as sender and the updated book as args.
                    subscriber.Invoke(this, _orderBook);
                }
                catch (Exception ex)
                {
                    // A single subscriber's error should not stop others.
                    _logger.LogErrorWithCaller(ex, $"An error occurred in a subscriber callback for {_instrument.Symbol}.");
                }
            }
        }
    }
}

