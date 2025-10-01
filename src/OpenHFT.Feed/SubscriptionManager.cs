using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;
namespace OpenHFT.Feed;

public class SubscriptionManager : ISubscriptionManager
{
    private readonly ILogger<SubscriptionManager> _logger;
    private readonly IFeedHandler _feedHandler;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly SubscriptionConfig _config;

    public SubscriptionManager(
        ILogger<SubscriptionManager> logger,
        IFeedHandler feedHandler,
        IInstrumentRepository instrumentRepository,
        SubscriptionConfig subscriptionConfig) // IOptions pattern is best practice
    {
        _logger = logger;
        _feedHandler = feedHandler;
        _instrumentRepository = instrumentRepository;
        _config = subscriptionConfig;
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
                var exchange = ParseEnum<ExchangeEnum>(group.Exchange);
                var productType = ParseProductType(group.ProductType);
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
            var instrumentsToSubscribe = entry.Value;
            var adapter = _feedHandler.GetAdapter(adapterKey.Item1, adapterKey.Item2);

            if (adapter != null)
            {
                _logger.LogInformationWithCaller($"Requesting subscription of {instrumentsToSubscribe.Count} instruments on adapter {adapterKey}");
                subscribeTasks.Add(adapter.SubscribeAsync(instrumentsToSubscribe, cancellationToken));
            }
            else
            {
                _logger.LogWarningWithCaller($"Adapter not found for {adapterKey}, cannot subscribe to {instrumentsToSubscribe.Count} instruments.");
            }
        }

        await Task.WhenAll(subscribeTasks);
        _logger.LogInformationWithCaller("All subscription requests have been submitted.");
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
}