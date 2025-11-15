using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Feed.Exceptions;
using OpenHFT.Feed.Models;

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
    /// Subscribe to market data for specific instruments
    /// </summary>
    Task SubscribeAsync(IEnumerable<Instrument> instruments, IEnumerable<ExchangeTopic> topics, CancellationToken cancellationToken = default);
    /// <summary>
    /// Unsubscribe from market data for specific instruments
    /// </summary>
    Task UnsubscribeAsync(IEnumerable<Instrument> instruments, IEnumerable<ExchangeTopic> topics, CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates the WebSocket connection to subscribe to private user data streams.
    /// </summary>
    Task AuthenticateAsync(string apiKey, string apiSecret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the name of the exchange this adapter connects to.
    /// Should match one of the constants in the `Exchange` class.
    /// </summary>
    ExchangeEnum SourceExchange { get; }

    /// <summary>
    /// Gets the product type (e.g., Spot, Futures) this adapter handles.
    /// </summary>
    ProductType ProdType { get; }

    /// <summary>
    /// Gets the execution mode(e.g., Live, Testnet)
    /// </summary>
    ExecutionMode ExecMode { get; }
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
    event EventHandler<AuthenticationEventArgs> AuthenticationStateChanged;


    /// <summary>
    /// Event fired when an error occurs
    /// </summary>
    event EventHandler<FeedErrorEventArgs> Error;

    /// <summary>
    /// Event fired when market data is received
    /// </summary>
    event EventHandler<MarketDataEvent> MarketDataReceived;

    /// <summary>
    /// Event fired when a user-specific order update is received.
    /// </summary>
    event EventHandler<OrderStatusReport> OrderUpdateReceived;
}

/// <summary>
/// Connection state change event arguments
/// </summary>
public class ConnectionStateChangedEventArgs : EventArgs
{
    public bool IsConnected { get; }
    public string? Reason { get; }
    public DateTimeOffset Timestamp { get; }
    public ExchangeEnum SourceExchange { get; }

    public ConnectionStateChangedEventArgs(bool isConnected, ExchangeEnum exchange, string? reason = null)
    {
        IsConnected = isConnected;
        Reason = reason;
        SourceExchange = exchange;
        Timestamp = DateTimeOffset.UtcNow;
    }
}

public class AuthenticationEventArgs : EventArgs
{
    public bool IsAuthenticated { get; }
    public string? Reason { get; }
    public DateTimeOffset Timestamp { get; }

    public AuthenticationEventArgs(bool isAuthenticated, string? reason = null)
    {
        IsAuthenticated = isAuthenticated;
        Reason = reason;
        Timestamp = DateTimeOffset.UtcNow;
    }
}
/// <summary>
/// Feed error event arguments
/// </summary>
public class FeedErrorEventArgs : EventArgs
{
    public FeedException Exception { get; }
    public string? Context { get; }
    public DateTimeOffset Timestamp { get; }

    public FeedErrorEventArgs(FeedException fe, string? context = null)
    {
        Exception = fe;
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
