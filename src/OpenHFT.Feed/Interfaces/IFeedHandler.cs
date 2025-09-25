using OpenHFT.Core.Collections;
using OpenHFT.Core.Models;

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
    void AddAdapter(IFeedAdapter adapter);

    /// <summary>
    /// Remove a feed adapter
    /// </summary>
    void RemoveAdapter(IFeedAdapter adapter);

    /// <summary>
    /// Get all registered adapters
    /// </summary>
    IReadOnlyList<IFeedAdapter> Adapters { get; }

    /// <summary>
    /// Event fired when market data is received (for monitoring)
    /// </summary>
    event EventHandler<MarketDataEvent> MarketDataReceived;

    /// <summary>
    /// Event fired when gap is detected in sequence
    /// </summary>
    event EventHandler<GapDetectedEventArgs> GapDetected;

    /// <summary>
    /// Get feed handler statistics
    /// </summary>
    FeedHandlerStatistics Statistics { get; }
}