using Disruptor.Dsl;
using OpenHFT.Core.Collections;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Feed.Adapters;

namespace OpenHFT.Feed.Interfaces;

/// <summary>
/// Market data feed handler that normalizes and publishes events
/// </summary>
public interface IFeedHandler : IDisposable
{
    /// <summary>
    /// Start processing market data from all adapters
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop processing market data
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a feed adapter
    /// </summary>
    void AddAdapter(BaseFeedAdapter adapter);

    /// <summary>
    /// Remove a feed adapter
    /// </summary>
    void RemoveAdapter(ExchangeEnum sourceExchange, ProductType type);

    /// <summary>
    /// get a feed adapter
    /// </summary>
    BaseFeedAdapter? GetAdapter(ExchangeEnum sourceExchange, ProductType type);

    /// <summary>
    /// Get feed handler statistics
    /// </summary>
    FeedHandlerStatistics Statistics { get; }

    event EventHandler<ConnectionStateChangedEventArgs> AdapterConnectionStateChanged;
}