using OpenHFT.Core.Models;
using OpenHFT.Book.Core;

namespace OpenHFT.Strategy.Interfaces;

/// <summary>
/// Base interface for all trading strategies
/// </summary>
public interface IStrategy : IDisposable
{
    /// <summary>
    /// Strategy name/identifier
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether the strategy is currently enabled
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Symbols this strategy trades
    /// </summary>
    IReadOnlyList<string> Symbols { get; }

    /// <summary>
    /// Initialize the strategy with configuration
    /// </summary>
    Task InitializeAsync(StrategyConfiguration configuration);

    /// <summary>
    /// Called when market data is received for subscribed symbols
    /// </summary>
    /// <param name="marketDataEvent">The market data event</param>
    /// <param name="orderBook">Current order book state</param>
    /// <returns>Collection of order intents to execute</returns>
    IEnumerable<OrderIntent> OnMarketData(MarketDataEvent marketDataEvent, OrderBook orderBook);

    /// <summary>
    /// Called when an order acknowledgment is received
    /// </summary>
    void OnOrderAck(OrderAck orderAck);

    /// <summary>
    /// Called when a fill is received
    /// </summary>
    void OnFill(FillEvent fillEvent);

    /// <summary>
    /// Called periodically for time-based logic
    /// </summary>
    IEnumerable<OrderIntent> OnTimer(DateTimeOffset timestamp);

    /// <summary>
    /// Start the strategy
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Stop the strategy
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Get current strategy state/metrics
    /// </summary>
    StrategyState GetState();

    /// <summary>
    /// Event fired when strategy generates an order intent
    /// </summary>
    event EventHandler<OrderIntent> OrderGenerated;

    /// <summary>
    /// Event fired when strategy state changes
    /// </summary>
    event EventHandler<StrategyStateChangedEventArgs> StateChanged;
}

/// <summary>
/// Strategy engine that manages multiple strategies
/// </summary>
public interface IStrategyEngine : IDisposable
{
    /// <summary>
    /// Register a strategy with the engine
    /// </summary>
    void RegisterStrategy(IStrategy strategy);

    /// <summary>
    /// Unregister a strategy from the engine
    /// </summary>
    void UnregisterStrategy(IStrategy strategy);

    /// <summary>
    /// Get all registered strategies
    /// </summary>
    IReadOnlyList<IStrategy> GetStrategies();

    /// <summary>
    /// Process market data through all enabled strategies
    /// </summary>
    IEnumerable<OrderIntent> ProcessMarketData(MarketDataEvent marketDataEvent, OrderBook orderBook);

    /// <summary>
    /// Process order acknowledgment through relevant strategies
    /// </summary>
    void ProcessOrderAck(OrderAck orderAck);

    /// <summary>
    /// Process fill through relevant strategies
    /// </summary>
    void ProcessFill(FillEvent fillEvent);

    /// <summary>
    /// Run timer-based logic for all strategies
    /// </summary>
    IEnumerable<OrderIntent> ProcessTimer(DateTimeOffset timestamp);

    /// <summary>
    /// Start all strategies
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Stop all strategies
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Event fired when any strategy generates an order
    /// </summary>
    event EventHandler<StrategyOrderEventArgs> OrderGenerated;

    /// <summary>
    /// Get engine statistics
    /// </summary>
    StrategyEngineStatistics GetStatistics();
}

/// <summary>
/// Strategy configuration
/// </summary>
public class StrategyConfiguration
{
    public string Name { get; set; } = "";
    public List<string> Symbols { get; set; } = new();
    public Dictionary<string, object> Parameters { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public decimal MaxPosition { get; set; } = 1000;
    public decimal MaxOrderSize { get; set; } = 100;
    public int MaxOrdersPerSecond { get; set; } = 10;
}

/// <summary>
/// Current state of a strategy
/// </summary>
public class StrategyState
{
    public string Name { get; set; } = "";
    public bool IsRunning { get; set; }
    public Dictionary<string, decimal> Positions { get; set; } = new();
    public Dictionary<string, int> ActiveOrders { get; set; } = new();
    public decimal UnrealizedPnL { get; set; }
    public decimal RealizedPnL { get; set; }
    public long OrderCount { get; set; }
    public long FillCount { get; set; }
    public long MessageCount { get; set; }
    public DateTimeOffset LastUpdate { get; set; }
    public Dictionary<string, object> Metrics { get; set; } = new();
}

/// <summary>
/// Event args for strategy state changes
/// </summary>
public class StrategyStateChangedEventArgs : EventArgs
{
    public string StrategyName { get; }
    public string PropertyName { get; }
    public object? OldValue { get; }
    public object? NewValue { get; }

    public StrategyStateChangedEventArgs(string strategyName, string propertyName, object? oldValue, object? newValue)
    {
        StrategyName = strategyName;
        PropertyName = propertyName;
        OldValue = oldValue;
        NewValue = newValue;
    }
}

/// <summary>
/// Event args for strategy orders
/// </summary>
public class StrategyOrderEventArgs : EventArgs
{
    public string StrategyName { get; }
    public OrderIntent OrderIntent { get; }
    public DateTimeOffset Timestamp { get; }

    public StrategyOrderEventArgs(string strategyName, OrderIntent orderIntent)
    {
        StrategyName = strategyName;
        OrderIntent = orderIntent;
        Timestamp = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Engine-level statistics
/// </summary>
public class StrategyEngineStatistics
{
    public int ActiveStrategies { get; set; }
    public long TotalOrdersGenerated { get; set; }
    public long TotalEventsProcessed { get; set; }
    public Dictionary<string, long> OrdersByStrategy { get; set; } = new();
    public Dictionary<string, long> MessagesByStrategy { get; set; } = new();
    public DateTimeOffset StartTime { get; set; }
    
    public double EventsPerSecond
    {
        get
        {
            var elapsed = (DateTimeOffset.UtcNow - StartTime).TotalSeconds;
            return elapsed > 0 ? TotalEventsProcessed / elapsed : 0;
        }
    }
}
