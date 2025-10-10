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
    /// <param name="instrument">The instrument to subscribe to.</param>
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

    /// <summary>
    /// Uninstalls a named callback for a specific instrument.
    /// </summary>
    /// <param name="instrument"="The instrument to unsubscribe from.</param>
    /// <param name="consumerName">The unique name of the consumer/callback to remove.</param>
    public void Uninstall(Instrument instrument, string consumerName)
    {
        if (string.IsNullOrWhiteSpace(consumerName))
        {
            _logger.LogWarningWithCaller($"Attempted to uninstall a consumer with a null or empty name for {instrument.Symbol}");
            return;
        }

        if (_consumers.TryGetValue(instrument.InstrumentId, out var consumer))
        {
            consumer.RemoveSubscriber(consumerName);
            _logger.LogInformationWithCaller($"consumer '{consumerName}' successfully uninstalled for InstrumentId {instrument.InstrumentId}.");
        }
        else
        {
            _logger.LogInformationWithCaller($"Cannot uninstall: No OrderBookConsumer found for InstrumentId {instrument.InstrumentId}.");
        }
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
                // 다른 거래소에 대한 case를 여기에 추가할 수 있습니다.
                // case ExchangeEnum.BYBIT: ...
        }

        throw new NotSupportedException($"No topic mapping found for consumer '{typeof(TConsumer).Name}' on exchange '{instrument.SourceExchange}'.");
    }
}
