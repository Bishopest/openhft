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

        var instrumentsByAdapter = new Dictionary<(ExchangeEnum, ProductType), List<Instrument>>();

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
                    instrumentsByAdapter[adapterKey] = new List<Instrument>();
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
                _logger.LogInformationWithCaller($"Requesting subscription of {instrumentsToSubscribe.Count} instruments on adapter {adapterKey}");
                subscribeTasks.Add(adapter.SubscribeAsync(instrumentsToSubscribe, cancellationToken));
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
        if (e.IsAuthenticated && sender is IFeedAdapter adapter)
        {
            // Run private topic subscription in the background to not block the event handler.
            _ = SubscribePrivateTopicsAsync(adapter);
        }
    }

    private async Task ResubscribeToAdapterAsync(IFeedAdapter adapter, CancellationToken cancellationToken = default)
    {
        _logger.LogInformationWithCaller($"Re-subscribing instruments for connected adapter: {adapter.SourceExchange}/{adapter.ProdType}");

        var instrumentsToSubscribe = new List<Instrument>();

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
                        _logger.LogWarning("Instrument not found for {Exchange}/{ProductType}/{Symbol} during re-subscription", group.Exchange, group.ProductType, symbolName);
                    }
                }
            }
        }

        if (instrumentsToSubscribe.Any())
        {
            _logger.LogInformationWithCaller($"Requesting re-subscription of {instrumentsToSubscribe.Count} instruments on adapter {adapter.SourceExchange}/{adapter.ProdType}");
            await adapter.SubscribeAsync(instrumentsToSubscribe, cancellationToken);
        }
    }

    private async Task SubscribePrivateTopicsAsync(IFeedAdapter adapter, CancellationToken cancellationToken = default)
    {
        if (adapter is not BaseAuthFeedAdapter authAdapter)
        {
            _logger.LogWarningWithCaller($"Adapter {adapter.SourceExchange} does not support authentication (BaseAuthFeedAdapter not implemented). Skipping private topics.");
            return;
        }

        var privateTopics = adapter.SourceExchange switch
        {
            ExchangeEnum.BINANCE => BinanceTopic.GetAllPrivateTopics(),
            ExchangeEnum.BITMEX => BitmexTopic.GetAllPrivateTopics(),
            _ => Enumerable.Empty<ExchangeTopic>()
        };

        if (!privateTopics.Any()) return;

        _logger.LogInformationWithCaller($"Subscribing to {privateTopics.Count()} private topics for {adapter.SourceExchange}/{adapter.ProdType}.");
        await authAdapter.SubscribeToPrivateTopicsAsync(cancellationToken);
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

    public void Dispose()
    {
        _feedHandler.AdapterConnectionStateChanged -= OnAdapterConnectionStateChanged;
    }
}