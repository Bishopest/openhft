using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed;

public class FeedAdapterRegistry : IFeedAdapterRegistry
{
    private readonly IReadOnlyDictionary<(ExchangeEnum, ProductType), IFeedAdapter> _adapters;
    private readonly ILogger<FeedAdapterRegistry> _logger;

    public FeedAdapterRegistry(ILogger<FeedAdapterRegistry> logger, IEnumerable<IFeedAdapter> adapters)
    {
        _logger = logger;
        _adapters = adapters.ToDictionary(
            adapter => (adapter.SourceExchange, adapter.ProdType),
            adapter => adapter);
        _logger.LogInformationWithCaller($"Registered {_adapters.Count} feed adapters.");
    }

    public IFeedAdapter? GetAdapter(ExchangeEnum exchange, ProductType productType)
    {
        if (_adapters.TryGetValue((exchange, productType), out var adapter))
        {
            return adapter;
        }
        _logger.LogWarningWithCaller($"Adapter for {exchange}/{productType} not found.");
        return null;
    }

    public IEnumerable<IFeedAdapter> GetAllAdapters()
    {
        return _adapters.Values;
    }
}