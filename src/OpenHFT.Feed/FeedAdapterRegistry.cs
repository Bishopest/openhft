using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed;

public class FeedAdapterRegistry : IFeedAdapterRegistry
{
    private readonly IReadOnlyDictionary<(ExchangeEnum Exchange, ProductType ProductType, StreamType StreamType), IFeedAdapter> _adapters;
    private readonly ILogger<FeedAdapterRegistry> _logger;

    public FeedAdapterRegistry(ILogger<FeedAdapterRegistry> logger, IEnumerable<IFeedAdapter> adapters)
    {
        _logger = logger;
        try
        {
            _adapters = adapters.ToDictionary(
                adapter => (adapter.SourceExchange, adapter.ProdType, adapter.StreamType),
                adapter => adapter);

            _logger.LogInformationWithCaller($"Registered {_adapters.Count} feed adapters.");
            foreach (var key in _adapters.Keys)
            {
                _logger.LogInformationWithCaller($"- Registered Adapter Key: ({key.Exchange}, {key.ProductType}, {key.ProductType})");
            }
        }
        catch (ArgumentException ex)
        {
            // This exception occurs if there are duplicate keys. Log detailed info.
            _logger.LogErrorWithCaller(ex, "Failed to build adapter dictionary. This is likely due to duplicate (Exchange, ProductType, StreamType) combinations being registered in the DI container.");
            // Re-throw or handle as appropriate for your application's startup sequence.
            throw;
        }

    }

    /// <summary>
    /// Gets a specific feed adapter based on its exchange, product type, and stream type.
    /// </summary>
    /// <param name="exchange">The target exchange.</param>
    /// <param name="productType">The target product type.</param>
    /// <param name="streamType">The stream type ("Public" or "Private"). Defaults to "Public".</param>
    /// <returns>The found IFeedAdapter, or null if not found.</returns>
    public IFeedAdapter? GetAdapter(ExchangeEnum exchange, ProductType productType, StreamType streamType = StreamType.PublicStream)
    {
        // --- MODIFICATION: Use the new three-part key for lookup ---
        var key = (exchange, productType, streamType);
        if (_adapters.TryGetValue(key, out var adapter))
        {
            return adapter;
        }

        _logger.LogWarningWithCaller($"Adapter for ({exchange}, {productType}, {streamType}) not found.");
        return null;
    }

    public IEnumerable<IFeedAdapter> GetAllAdapters()
    {
        return _adapters.Values;
    }
}