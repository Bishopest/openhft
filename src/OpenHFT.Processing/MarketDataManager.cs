using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Models;
using OpenHFT.Processing.Consumers;

namespace OpenHFT.Processing;

public class MarketDataManager
{
    private readonly ILogger<MarketDataManager> _logger;
    private readonly MarketDataDistributor _distributor;
    private readonly IInstrumentRepository _repository;
    private readonly ConcurrentDictionary<int, OrderBookConsumer> _consumers = new();
    private readonly ConcurrentDictionary<int, BestOrderBookConsumer> _bestOrderBookConsumers = new();


    public MarketDataManager(
        ILogger<MarketDataManager> logger,
        MarketDataDistributor distributor,
        IInstrumentRepository repository,
        SubscriptionConfig config
    )
    {
        _logger = logger;
        _repository = repository;
        _distributor = distributor;

        Initialize(config);
    }

    private void Initialize(SubscriptionConfig config)
    {
        foreach (var group in config.Subscriptions)
        {
            if (!Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange) ||
                !Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
            {
                continue;
            }

            foreach (var symbol in group.Symbols)
            {
                var instrument = _repository.FindBySymbol(symbol, productType, exchange);
                if (instrument == null)
                {
                    _logger.LogWarningWithCaller($"Could not find symbol {symbol} prod-type {productType} on exchange {exchange}.");
                    continue;
                }

                Install(instrument);
            }
        }
    }

    /// <summary>
    /// Installs a named callback for a specific instrument.
    /// If it's the first subscription for the instrument, it creates the necessary consumer and data feed connection.
    /// </summary>
    /// <param name="instrument">The instrument to install consumers for.</param>
    /// <param name="consumerName">A unique name for this specific consumer/callback.</param>
    public void Install(Instrument instrument)
    {
        // Atomically get or create the OrderBookConsumer for this instrument.
        var orderBookConsumer = _consumers.GetOrAdd(instrument.InstrumentId, (id) =>
        {
            _logger.LogInformationWithCaller($"First subscriber for {instrument.Symbol} ({id}). Creating new OrderBookConsumer and requesting data feed.");

            var obConsumer = new OrderBookConsumer(instrument, _logger, GetTopicForConsumer<OrderBookConsumer>(instrument));

            // 1. Register the new consumer to receive raw market data.
            _distributor.Subscribe(obConsumer);
            return obConsumer;
        });

        // Atomically get or create the BestOrderBookConsumer for this instrument.
        var bestOrderBookConsumer = _bestOrderBookConsumers.GetOrAdd(instrument.InstrumentId, (id) =>
        {
            _logger.LogInformationWithCaller($"First subscriber for {instrument.Symbol} ({id}). Creating new BestOrderBookConsumer and requesting data feed.");

            var bobConsumer = new BestOrderBookConsumer(instrument, _logger, GetTopicForConsumer<BestOrderBookConsumer>(instrument));

            // Register the new consumer to receive raw market data.
            _distributor.Subscribe(bobConsumer);
            return bobConsumer;
        });

        _logger.LogInformationWithCaller($"market data consumer successfully installed for {instrument.Symbol}.");
    }

    public void SubscribeOrderBook(Instrument instrument, string subscriberName, EventHandler<OrderBook> callback)
    {
        if (_consumers.TryGetValue(instrument.InstrumentId, out var consumer))
        {
            consumer.AddSubscriber(subscriberName, callback);
        }
        else
        {
            _logger.LogWarningWithCaller($"No OrderBookConsumer found for {instrument.Symbol}. Cannot subscribe '{subscriberName}'. Please ensure Install() is called first.");
        }
    }

    public void UnsubscribeOrderBook(Instrument instrument, string subscriberName)
    {
        if (_consumers.TryGetValue(instrument.InstrumentId, out var consumer))
        {
            consumer.RemoveSubscriber(subscriberName);
        }
        else
        {
            _logger.LogWarningWithCaller($"No OrderBookConsumer found for {instrument.Symbol}. Cannot unsubscribe '{subscriberName}'.");
        }
    }

    public void SubscribeBestOrderBook(Instrument instrument, string subscriberName, EventHandler<BestOrderBook> callback)
    {
        if (_bestOrderBookConsumers.TryGetValue(instrument.InstrumentId, out var consumer))
        {
            consumer.AddSubscriber(subscriberName, callback);
        }
        else
        {
            _logger.LogWarningWithCaller($"No BestOrderBookConsumer found for {instrument.Symbol}. Cannot subscribe '{subscriberName}'. Please ensure Install() is called first.");
        }
    }

    public void UnsubscribeBestOrderBook(Instrument instrument, string subscriberName)
    {
        if (_bestOrderBookConsumers.TryGetValue(instrument.InstrumentId, out var consumer))
        {
            consumer.RemoveSubscriber(subscriberName);
        }
        else
        {
            _logger.LogWarningWithCaller($"No BestOrderBookConsumer found for {instrument.Symbol}. Cannot unsubscribe '{subscriberName}'.");
        }
    }

    /// <summary>
    /// Uninstalls a named callback for a specific instrument.
    /// This is now a legacy method. Use UnsubscribeOrderBook or UnsubscribeBestOrderBook instead.
    /// </summary>
    /// <param name="instrument">The instrument to unsubscribe from.</param>
    /// <param name="subscriberName">The unique name of the subscriber/callback to remove.</param>
    [Obsolete("Use UnsubscribeOrderBook or UnsubscribeBestOrderBook for clarity.")]
    public void Uninstall(Instrument instrument, string subscriberName)
    {
        if (string.IsNullOrWhiteSpace(subscriberName))
        {
            _logger.LogWarningWithCaller($"Attempted to uninstall a subscriber with a null or empty name for {instrument.Symbol}");
            return;
        }

        UnsubscribeOrderBook(instrument, subscriberName);
        UnsubscribeBestOrderBook(instrument, subscriberName);
    }

    private ExchangeTopic GetTopicForConsumer<TConsumer>(Instrument instrument) where TConsumer : BaseMarketDataConsumer
    {
        switch (instrument.SourceExchange)
        {
            case ExchangeEnum.BINANCE:
                if (typeof(TConsumer) == typeof(OrderBookConsumer))
                {
                    return BinanceTopic.DepthUpdate;
                }
                if (typeof(TConsumer) == typeof(BestOrderBookConsumer))
                {
                    return BinanceTopic.BookTicker;
                }
                break;
            case ExchangeEnum.BITMEX:
                if (typeof(TConsumer) == typeof(OrderBookConsumer))
                {
                    return BitmexTopic.OrderBook10;
                }
                if (typeof(TConsumer) == typeof(BestOrderBookConsumer))
                {
                    return BitmexTopic.Quote;
                }
                break;
                // 다른 거래소에 대한 case를 여기에 추가할 수 있습니다.
                // case ExchangeEnum.BYBIT: ...
        }

        throw new NotSupportedException($"No topic mapping found for consumer '{typeof(TConsumer).Name}' on exchange '{instrument.SourceExchange}'.");
    }
}
