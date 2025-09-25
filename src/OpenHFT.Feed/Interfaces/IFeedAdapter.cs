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
    /// Gets the name of the exchange this adapter connects to.
    /// Should match one of the constants in the `Exchange` class.
    /// </summary>
    string ExchangeName { get; }

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
