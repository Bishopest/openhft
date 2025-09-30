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
        SubscriptionConfig configOptions) // IOptions pattern is best practice
    {
        _logger = logger;
        _feedHandler = feedHandler;
        _instrumentRepository = instrumentRepository;
        _config = configOptions;
    }

    public async Task InitializeSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformationWithCaller("Initializing subscriptions based on config.json...");

        int totalSymbolsConfigured = 0;

        // The _config object itself is the dictionary of exchanges
        foreach (var exchangeEntry in _config)
        {
            var exchangeName = exchangeEntry.Key;

            foreach (var productTypeEntry in exchangeEntry.Value)
            {
                var productTypeName = productTypeEntry.Key;
                var symbols = productTypeEntry.Value;

                if (symbols == null || !symbols.Any()) continue;

                var instrumentsToSubscribe = new List<Instrument>();
                var exchange = ParseEnum<ExchangeEnum>(exchangeName);
                var productType = ParseProductType(productTypeName);
                foreach (var symbolName in symbols)
                {
                    totalSymbolsConfigured++;
                    try
                    {
                        var instrument = _instrumentRepository.FindBySymbol(symbolName, productType, exchange);

                        if (instrument != null)
                        {
                            instrumentsToSubscribe.Add(instrument);
                        }
                        else
                        {
                            _logger.LogWarningWithCaller($"Instrument not found in repository for: [Exchange: {exchangeName}, ProductType: {productTypeName}, Symbol: {symbolName}]. Skipping.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogErrorWithCaller(ex, $"Failed to process subscription entry: [Exchange: {exchangeName}, ProductType: {productTypeName}, Symbol: {symbolName}]");
                    }
                }

                if (instrumentsToSubscribe.Any())
                {
                    var adapter = _feedHandler.GetAdapter(exchange, productType);
                    if (adapter != null)
                    {
                        await adapter.SubscribeAsync(instrumentsToSubscribe, cancellationToken);
                        _logger.LogInformationWithCaller($"Processed to subscribe {totalSymbolsConfigured} symbols from exchange {exchange}-{productTypeName}");
                    }
                }
                else
                {
                    _logger.LogWarningWithCaller("No valid instruments to subscribe to. The system will not receive market data.");
                }
            }
        }
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