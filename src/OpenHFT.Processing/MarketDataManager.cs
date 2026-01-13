using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed;
using OpenHFT.Feed.Models;
using OpenHFT.Processing.Consumers;

namespace OpenHFT.Processing;

public class MarketDataManager : IMarketDataManager
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

        _logger.LogInformationWithCaller("Installing consumers for required FX rate instruments...");
        foreach (var req in FxRateManagerBase.GetRequiredFxInstruments())
        {
            var instrument = _repository.GetAll().FirstOrDefault(i =>
                i.SourceExchange == req.Exchange && i.ProductType == req.ProductType &&
                i.BaseCurrency == req.Base && i.QuoteCurrency == req.Quote);

            if (instrument != null)
            {
                _logger.LogInformationWithCaller($"Auto-installing FX reference instrument: {instrument.Symbol}");
                Install(instrument);
            }
            else
            {
                _logger.LogWarningWithCaller($"Required FX reference instrument not found: {req.Base}/{req.Quote} on {req.Exchange}/{req.ProductType}");
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
        // --- OrderBookConsumer Installation ---
        var orderBookTopic = GetTopicForConsumer<OrderBookConsumer>(instrument);

        if (orderBookTopic != null)
        {
            _consumers.GetOrAdd(instrument.InstrumentId, (id) =>
            {
                _logger.LogInformationWithCaller($"Creating new OrderBookConsumer for {instrument.Symbol} on topic {orderBookTopic.GetTopicName()}.");

                var obConsumer = new OrderBookConsumer(instrument, _logger, orderBookTopic);
                _distributor.Subscribe(obConsumer);
                return obConsumer;
            });
        }
        else
        {
            _logger.LogWarningWithCaller($"No suitable topic found for OrderBookConsumer on {instrument.SourceExchange} for instrument {instrument.Symbol}. This consumer will not be installed.");
        }

        // --- BestOrderBookConsumer Installation ---
        var bestOrderBookTopic = GetTopicForConsumer<BestOrderBookConsumer>(instrument);

        if (bestOrderBookTopic != null)
        {
            _bestOrderBookConsumers.GetOrAdd(instrument.InstrumentId, (id) =>
            {
                _logger.LogInformationWithCaller($"Creating new BestOrderBookConsumer for {instrument.Symbol} on topic {bestOrderBookTopic.GetTopicName()}.");

                var bobConsumer = new BestOrderBookConsumer(instrument, _logger, bestOrderBookTopic);
                _distributor.Subscribe(bobConsumer);
                return bobConsumer;
            });
        }
        else
        {
            _logger.LogWarningWithCaller($"No suitable topic found for BestOrderBookConsumer on {instrument.SourceExchange} for instrument {instrument.Symbol}. This consumer will not be installed.");
        }
    }

    public void SubscribeOrderBook(int instrumentId, string subscriberName, EventHandler<OrderBook> callback)
    {
        var instrument = _repository.GetById(instrumentId);
        if (instrument == null)
        {
            _logger.LogWarningWithCaller($"Could not find symbol by id {instrumentId}.");
            return;
        }

        if (_consumers.TryGetValue(instrumentId, out var consumer))
        {
            consumer.AddSubscriber(subscriberName, callback);
            _logger.LogInformationWithCaller($"Start to subscribe orderbook by {subscriberName} for symbol {instrument.Symbol}.");
        }
        else
        {
            _logger.LogWarningWithCaller($"No OrderBookConsumer found for {instrument.Symbol}. Cannot subscribe '{subscriberName}'. Please ensure Install() is called first.");
        }
    }

    public void UnsubscribeOrderBook(int instrumentId, string subscriberName)
    {
        var instrument = _repository.GetById(instrumentId);
        if (instrument == null)
        {
            _logger.LogWarningWithCaller($"Could not find symbol by id {instrumentId}.");
            return;
        }

        if (_consumers.TryGetValue(instrumentId, out var consumer))
        {
            consumer.RemoveSubscriber(subscriberName);
        }
        else
        {
            _logger.LogWarningWithCaller($"No OrderBookConsumer found for {instrument.Symbol}. Cannot unsubscribe '{subscriberName}'.");
        }
    }

    public void SubscribeBestOrderBook(int instrumentId, string subscriberName, EventHandler<BestOrderBook> callback)
    {
        var instrument = _repository.GetById(instrumentId);
        if (instrument == null)
        {
            _logger.LogWarningWithCaller($"Could not find symbol by id {instrumentId}.");
            return;
        }

        if (_bestOrderBookConsumers.TryGetValue(instrumentId, out var consumer))
        {
            consumer.AddSubscriber(subscriberName, callback);
        }
        else
        {
            _logger.LogWarningWithCaller($"No BestOrderBookConsumer found for {instrument.Symbol}. Cannot subscribe '{subscriberName}'. Please ensure Install() is called first.");
        }
    }

    public void UnsubscribeBestOrderBook(int instrumentId, string subscriberName)
    {
        var instrument = _repository.GetById(instrumentId);
        if (instrument == null)
        {
            _logger.LogWarningWithCaller($"Could not find symbol by id {instrumentId}.");
            return;
        }

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

        UnsubscribeOrderBook(instrument.InstrumentId, subscriberName);
        UnsubscribeBestOrderBook(instrument.InstrumentId, subscriberName);
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
            case ExchangeEnum.BITHUMB:
                if (typeof(TConsumer) == typeof(OrderBookConsumer))
                {
                    return BithumbTopic.OrderBook;
                }
                else if (typeof(TConsumer) == typeof(BestOrderBookConsumer))
                {
                    return null;
                }
                break;
                // 다른 거래소에 대한 case를 여기에 추가할 수 있습니다.
                // case ExchangeEnum.BYBIT: ...
        }

        throw new NotSupportedException($"No topic mapping found for consumer '{typeof(TConsumer).Name}' on exchange '{instrument.SourceExchange}'.");
    }

    public OrderBook? GetOrderBook(int instrumentId)
    {
        if (_consumers.TryGetValue(instrumentId, out var consumer))
        {
            return consumer.Book;
        }
        return null;
    }

    public BestOrderBook? GetBestOrderBook(int instrumentId)
    {
        if (_bestOrderBookConsumers.TryGetValue(instrumentId, out var consumer))
        {
            return consumer.BestBook;
        }
        return null;
    }
}
