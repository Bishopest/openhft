using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;
namespace OpenHFT.Feed;

public class SubscriptionManager : ISubscriptionManager, IDisposable
{
    private readonly ILogger<SubscriptionManager> _logger;
    private readonly IFeedHandler _feedHandler;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly SubscriptionConfig _config;
    private readonly IFeedAdapterRegistry _adapterRegistry;
    public SubscriptionManager(
        ILogger<SubscriptionManager> logger,
        IFeedHandler feedHandler,
        IFeedAdapterRegistry adapterRegistry,
        IInstrumentRepository instrumentRepository,
        SubscriptionConfig subscriptionConfig) // IOptions pattern is best practice
    {
        _logger = logger;
        _feedHandler = feedHandler;
        _adapterRegistry = adapterRegistry;
        _instrumentRepository = instrumentRepository;
        _config = subscriptionConfig;

        _logger.LogInformationWithCaller($"SubscriptionManager initialized with config: {_config}");
        _feedHandler.AdapterConnectionStateChanged += OnAdapterConnectionStateChanged;
        _feedHandler.AdapterAuthenticationStateChanged += OnAdapterAuthenticationStateChanged;
    }

    public async Task InitializeSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformationWithCaller("Initializing subscriptions from config.json...");

        var instrumentsByAdapter = new Dictionary<(ExchangeEnum, ProductType), HashSet<Instrument>>();

        // The _config object itself is the dictionary of exchanges
        foreach (var group in _config.Subscriptions)
        {
            if (group.Symbols == null || !group.Symbols.Any()) continue;

            try
            {
                if (!Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange) ||
                    !Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
                {
                    continue;
                }

                var adapterKey = (exchange, productType);

                if (!instrumentsByAdapter.ContainsKey(adapterKey))
                {
                    instrumentsByAdapter[adapterKey] = new HashSet<Instrument>();
                }

                foreach (var symbolName in group.Symbols)
                {
                    var instrument = _instrumentRepository.FindBySymbol(symbolName, productType, exchange);
                    if (instrument != null)
                    {
                        instrumentsByAdapter[adapterKey].Add(instrument);
                    }
                    else
                    {
                        _logger.LogWarning("Instrument not found for {Exchange}/{ProductType}/{Symbol}", group.Exchange, group.ProductType, symbolName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process subscription group: {@Group}", group);
            }
        }

        // Automatically add all required FX instruments to the subscription list.
        _logger.LogInformationWithCaller("Adding required FX rate instruments to subscription list...");
        foreach (var req in FxRateManagerBase.GetRequiredFxInstruments())
        {
            var instrument = _instrumentRepository.GetAll().FirstOrDefault(i =>
                i.SourceExchange == req.Exchange && i.ProductType == req.ProductType &&
                i.BaseCurrency == req.Base && i.QuoteCurrency == req.Quote);

            if (instrument != null)
            {
                var adapterKey = (instrument.SourceExchange, instrument.ProductType);
                if (!instrumentsByAdapter.ContainsKey(adapterKey))
                {
                    instrumentsByAdapter[adapterKey] = new HashSet<Instrument>();
                }
                if (instrumentsByAdapter[adapterKey].Add(instrument))
                {
                    _logger.LogInformationWithCaller($"Auto-subscribing to FX reference instrument: {instrument.Symbol}");
                }
            }
            else
            {
                _logger.LogWarningWithCaller($"Required FX reference instrument not found: {req.Base}/{req.Quote} on {req.Exchange}/{req.ProductType}");
            }
        }

        var subscribeTasks = new List<Task>();
        foreach (var entry in instrumentsByAdapter)
        {
            var adapterKey = entry.Key;
            var adapter = _adapterRegistry.GetAdapter(adapterKey.Item1, adapterKey.Item2);
            if (adapter == null)
            {
                _logger.LogWarningWithCaller($"Adapter not found for {adapterKey}, cannot subscribe to {entry.Value.Count} instruments.");
                continue;
            }

            var instrumentsToSubscribe = entry.Value;
            if (instrumentsToSubscribe.Any())
            {
                var topicsToSubscribe = GetDefaultTopicsForExchange(adapterKey.Item1);
                _logger.LogInformationWithCaller($"Requesting subscription of {instrumentsToSubscribe.Count} instruments to {topicsToSubscribe.Count()} topics on adapter {adapterKey}");
                subscribeTasks.Add(adapter.SubscribeAsync(instrumentsToSubscribe, topicsToSubscribe, cancellationToken));
            }
        }

        await Task.WhenAll(subscribeTasks);
        _logger.LogInformationWithCaller("All subscription requests have been submitted.");
    }

    private void OnAdapterConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (e.IsConnected && sender is IFeedAdapter adapter)
        {
            // Run re-subscription in the background to not block the event handler.
            _ = ResubscribeToAdapterAsync(adapter);
        }
    }

    private void OnAdapterAuthenticationStateChanged(object? sender, AuthenticationEventArgs e)
    {
        if (e.IsAuthenticated && sender is BaseAuthFeedAdapter adapter)
        {
            // Run private topic subscription in the background to not block the event handler.
            _ = SubscribePrivateTopicsAsync(adapter);
        }
    }

    private async Task ResubscribeToAdapterAsync(IFeedAdapter adapter, CancellationToken cancellationToken = default)
    {
        _logger.LogInformationWithCaller($"Re-subscribing instruments for connected adapter: {adapter.SourceExchange}/{adapter.ProdType}");

        var instrumentsToSubscribe = new HashSet<Instrument>();

        foreach (var group in _config.Subscriptions)
        {
            if (!Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange) ||
                !Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
            {
                continue;
            }

            if (exchange == adapter.SourceExchange && productType == adapter.ProdType)
            {
                foreach (var symbolName in group.Symbols)
                {
                    var instrument = _instrumentRepository.FindBySymbol(symbolName, productType, exchange);
                    if (instrument != null)
                    {
                        instrumentsToSubscribe.Add(instrument);
                    }
                    else
                    {
                        _logger.LogWarningWithCaller($"Instrument not found for {group.Exchange}/{group.ProductType}/{symbolName} during re-subscription");
                    }
                }
            }
        }

        foreach (var req in FxRateManagerBase.GetRequiredFxInstruments())
        {
            if (req.Exchange == adapter.SourceExchange && req.ProductType == adapter.ProdType)
            {
                var instrument = _instrumentRepository.GetAll().FirstOrDefault(i =>
                    i.BaseCurrency == req.Base && i.QuoteCurrency == req.Quote &&
                    i.SourceExchange == req.Exchange && i.ProductType == req.ProductType);

                if (instrument != null) instrumentsToSubscribe.Add(instrument);
            }
        }

        if (instrumentsToSubscribe.Any())
        {
            var topicsToSubscribe = GetDefaultTopicsForExchange(adapter.SourceExchange);
            _logger.LogInformationWithCaller($"Requesting re-subscription of {instrumentsToSubscribe.Count} instruments to {topicsToSubscribe.Count()} topics on adapter {adapter.SourceExchange}/{adapter.ProdType}");
            await adapter.SubscribeAsync(instrumentsToSubscribe.ToList(), topicsToSubscribe, cancellationToken);
        }
    }

    private async Task SubscribePrivateTopicsAsync(BaseAuthFeedAdapter adapter, CancellationToken cancellationToken = default)
    {
        await adapter.SubscribeToPrivateTopicsAsync(cancellationToken);
    }

    private T ParseEnum<T>(string value) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, true, out var result))
        {
            return result;
        }
        throw new ArgumentException($"'{value}' is not a valid value for enum {typeof(T).Name}.");
    }

    // Helper to map JSON-friendly names to Enum names if they differ
    private ProductType ParseProductType(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "perpetual" => ProductType.PerpetualFuture,
            "spot" => ProductType.Spot,
            "dated" => ProductType.DatedFuture,
            "option" => ProductType.Option,
            _ => ParseEnum<ProductType>(value) // Fallback for direct matches
        };
    }

    /// <summary>
    /// A helper method to get the default market data topics for a given exchange.
    /// </summary>
    private IEnumerable<ExchangeTopic> GetDefaultTopicsForExchange(ExchangeEnum exchange)
    {
        return exchange switch
        {
            ExchangeEnum.BINANCE => BinanceTopic.GetAllMarketTopics(),
            ExchangeEnum.BITMEX => BitmexTopic.GetAllMarketTopics(),
            ExchangeEnum.BITHUMB => BithumbTopic.GetAllMarketTopics(),
            // Add other exchanges here
            _ => Enumerable.Empty<ExchangeTopic>()
        };
    }

    public void Dispose()
    {
        _feedHandler.AdapterConnectionStateChanged -= OnAdapterConnectionStateChanged;
    }
}