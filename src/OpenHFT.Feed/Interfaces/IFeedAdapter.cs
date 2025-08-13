using OpenHFT.Core.Models;
using OpenHFT.Core.Collections;

namespace OpenHFT.Feed.Interfaces;

/// <summary>
/// Base interface for all market data feed adapters
/// </summary>
public interface IFeedAdapter : IDisposable
{
    /// <summary>
    /// Connect to the market data source
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from the market data source
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to market data for specific symbols
    /// </summary>
    Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unsubscribe from market data for specific symbols
    /// </summary>
    Task UnsubscribeAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default);

    /// <summary>
    /// Start processing market data
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop processing market data
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if adapter is connected
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Get current connection status
    /// </summary>
    string Status { get; }

    /// <summary>
    /// Event fired when connection state changes
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

    /// <summary>
    /// Event fired when an error occurs
    /// </summary>
    event EventHandler<FeedErrorEventArgs> Error;

    /// <summary>
    /// Event fired when market data is received
    /// </summary>
    event EventHandler<MarketDataEvent> MarketDataReceived;

    /// <summary>
    /// Statistics about the feed
    /// </summary>
    FeedStatistics Statistics { get; }
}

/// <summary>
/// Market data feed handler that normalizes and publishes events
/// </summary>
public interface IFeedHandler : IDisposable
{
    /// <summary>
    /// Initialize the feed handler with market data queue
    /// </summary>
    void Initialize(LockFreeRingBuffer<MarketDataEvent> marketDataQueue);

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

/// <summary>
/// Connection state change event arguments
/// </summary>
public class ConnectionStateChangedEventArgs : EventArgs
{
    public bool IsConnected { get; }
    public string? Reason { get; }
    public DateTimeOffset Timestamp { get; }

    public ConnectionStateChangedEventArgs(bool isConnected, string? reason = null)
    {
        IsConnected = isConnected;
        Reason = reason;
        Timestamp = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Feed error event arguments
/// </summary>
public class FeedErrorEventArgs : EventArgs
{
    public Exception Exception { get; }
    public string? Context { get; }
    public DateTimeOffset Timestamp { get; }

    public FeedErrorEventArgs(Exception exception, string? context = null)
    {
        Exception = exception;
        Context = context;
        Timestamp = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Gap detected event arguments
/// </summary>
public class GapDetectedEventArgs : EventArgs
{
    public string Symbol { get; }
    public long ExpectedSequence { get; }
    public long ReceivedSequence { get; }
    public DateTimeOffset Timestamp { get; }

    public GapDetectedEventArgs(string symbol, long expectedSequence, long receivedSequence)
    {
        Symbol = symbol;
        ExpectedSequence = expectedSequence;
        ReceivedSequence = receivedSequence;
        Timestamp = DateTimeOffset.UtcNow;
    }
}
