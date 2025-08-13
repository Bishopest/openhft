using OpenHFT.Core.Models;
using OpenHFT.Book.Core;
using OpenHFT.Strategy.Interfaces;

namespace OpenHFT.Strategy.Strategies;

/// <summary>
/// Base class for all trading strategies
/// Provides common functionality and state management
/// </summary>
public abstract class BaseStrategy : IStrategy
{
    protected StrategyConfiguration Configuration { get; private set; } = new();
    protected StrategyState State { get; private set; } = new();
    
    private readonly object _stateLock = new();
    private bool _isDisposed;

    public string Name { get; }
    public bool IsEnabled { get; set; } = true;
    public bool IsRunning { get; private set; }
    public IReadOnlyList<string> Symbols => Configuration.Symbols.AsReadOnly();

    public event EventHandler<OrderIntent>? OrderGenerated;
    public event EventHandler<StrategyStateChangedEventArgs>? StateChanged;

    protected BaseStrategy(string name)
    {
        Name = name;
        State.Name = name;
        State.LastUpdate = DateTimeOffset.UtcNow;
    }

    public virtual async Task InitializeAsync(StrategyConfiguration configuration)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        
        State.Name = configuration.Name.IsNullOrEmpty() ? Name : configuration.Name;
        IsEnabled = configuration.Enabled;
        
        // Initialize positions for all symbols
        foreach (var symbol in configuration.Symbols)
        {
            State.Positions[symbol] = 0;
            State.ActiveOrders[symbol] = 0;
        }

        await Task.CompletedTask;
    }

    public abstract IEnumerable<OrderIntent> OnMarketData(MarketDataEvent marketDataEvent, OrderBook orderBook);

    public virtual void OnOrderAck(OrderAck orderAck)
    {
        lock (_stateLock)
        {
            State.LastUpdate = DateTimeOffset.UtcNow;
        }
    }

    public virtual void OnFill(FillEvent fillEvent)
    {
        lock (_stateLock)
        {
            State.FillCount++;
            State.LastUpdate = DateTimeOffset.UtcNow;
        }
    }

    public virtual IEnumerable<OrderIntent> OnTimer(DateTimeOffset timestamp)
    {
        return Enumerable.Empty<OrderIntent>();
    }

    public virtual async Task StartAsync()
    {
        IsRunning = true;
        OnStateChanged(nameof(IsRunning), false, true);
        await Task.CompletedTask;
    }

    public virtual async Task StopAsync()
    {
        IsRunning = false;
        OnStateChanged(nameof(IsRunning), true, false);
        await Task.CompletedTask;
    }

    public virtual StrategyState GetState()
    {
        lock (_stateLock)
        {
            // Return a copy to avoid concurrent modifications
            return new StrategyState
            {
                Name = State.Name,
                IsRunning = IsRunning,
                Positions = new Dictionary<string, decimal>(State.Positions),
                ActiveOrders = new Dictionary<string, int>(State.ActiveOrders),
                UnrealizedPnL = State.UnrealizedPnL,
                RealizedPnL = State.RealizedPnL,
                OrderCount = State.OrderCount,
                FillCount = State.FillCount,
                MessageCount = State.MessageCount,
                LastUpdate = State.LastUpdate,
                Metrics = new Dictionary<string, object>(State.Metrics)
            };
        }
    }

    protected void IncrementOrderCount()
    {
        lock (_stateLock)
        {
            State.OrderCount++;
        }
    }

    protected void IncrementMessageCount()
    {
        lock (_stateLock)
        {
            State.MessageCount++;
        }
    }

    protected void UpdatePosition(string symbol, decimal quantity)
    {
        lock (_stateLock)
        {
            var oldPosition = State.Positions.GetValueOrDefault(symbol, 0);
            State.Positions[symbol] = oldPosition + quantity;
            OnStateChanged($"Position.{symbol}", oldPosition, State.Positions[symbol]);
        }
    }

    protected void UpdateActiveOrderCount(string symbol, int delta)
    {
        lock (_stateLock)
        {
            var oldCount = State.ActiveOrders.GetValueOrDefault(symbol, 0);
            State.ActiveOrders[symbol] = Math.Max(0, oldCount + delta);
            OnStateChanged($"ActiveOrders.{symbol}", oldCount, State.ActiveOrders[symbol]);
        }
    }

    protected void OnOrderGenerated(OrderIntent orderIntent)
    {
        OrderGenerated?.Invoke(this, orderIntent);
    }

    protected void OnStateChanged(string propertyName, object? oldValue, object? newValue)
    {
        StateChanged?.Invoke(this, new StrategyStateChangedEventArgs(Name, propertyName, oldValue, newValue));
    }

    public virtual void Dispose()
    {
        if (_isDisposed) return;

        try
        {
            if (IsRunning)
            {
                StopAsync().Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // Ignore errors during disposal
        }

        _isDisposed = true;
    }
}

/// <summary>
/// Extension methods for string operations
/// </summary>
public static class StringExtensions
{
    public static bool IsNullOrEmpty(this string? str) => string.IsNullOrEmpty(str);
}
