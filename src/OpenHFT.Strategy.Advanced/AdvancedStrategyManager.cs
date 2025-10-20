using OpenHFT.Core.Models;
using OpenHFT.Book.Models;
using OpenHFT.Book.Core;
using OpenHFT.Strategy.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace OpenHFT.Strategy.Advanced;

/// <summary>
/// Advanced strategy manager that coordinates multiple sophisticated trading strategies,
/// handles risk management, and provides comprehensive performance analytics.
/// </summary>
public class AdvancedStrategyManager : BackgroundService, IAdvancedStrategyManager
{
    private readonly ILogger<AdvancedStrategyManager> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, IAdvancedStrategy> _strategies;
    private readonly ConcurrentDictionary<string, StrategyAllocation> _allocations;
    private readonly ConcurrentQueue<StrategyEvent> _eventQueue;

    // Risk management
    private readonly RiskManager _riskManager;
    private readonly PerformanceAnalyzer _performanceAnalyzer;

    // Configuration
    private readonly AdvancedStrategyConfig _config;
    private bool _isRunning;
    private readonly CancellationTokenSource _cancellationTokenSource;

    // Performance tracking
    private long _totalOrdersGenerated;
    private long _totalOrdersExecuted;
    private decimal _totalPnL;
    private readonly object _statsLock = new();

    public AdvancedStrategyManager(
        ILogger<AdvancedStrategyManager> logger,
        IServiceProvider serviceProvider,
        AdvancedStrategyConfig config)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _config = config;
        _strategies = new ConcurrentDictionary<string, IAdvancedStrategy>();
        _allocations = new ConcurrentDictionary<string, StrategyAllocation>();
        _eventQueue = new ConcurrentQueue<StrategyEvent>();
        _riskManager = new RiskManager(logger, config.RiskConfig);
        _performanceAnalyzer = new PerformanceAnalyzer(logger);
        _cancellationTokenSource = new CancellationTokenSource();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Advanced Strategy Manager starting...");

        try
        {
            // Initialize strategies
            await InitializeStrategies();

            // Start risk management
            await _riskManager.StartAsync();

            _isRunning = true;

            // Main processing loop
            while (!stoppingToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    await ProcessStrategyEvents(stoppingToken);
                    await UpdatePerformanceMetrics();
                    await _riskManager.MonitorRisk();

                    // Brief pause to prevent excessive CPU usage
                    await Task.Delay(1, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in strategy manager main loop");
                    await Task.Delay(100, stoppingToken); // Brief pause on error
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in Advanced Strategy Manager");
        }
        finally
        {
            await StopStrategies();
            await _riskManager.StopAsync();
            _logger.LogInformation("Advanced Strategy Manager stopped");
        }
    }

    /// <summary>
    /// Initialize all advanced strategies with proper configuration
    /// </summary>
    private async Task InitializeStrategies()
    {
        _logger.LogInformation("Initializing advanced strategies...");

        try
        {
            _logger.LogInformation("Initialized {StrategyCount} advanced strategies", _strategies.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize strategies");
            throw;
        }
    }

    /// <summary>
    /// Register a strategy with the manager
    /// </summary>
    public async Task RegisterStrategy(IAdvancedStrategy strategy, StrategyAllocation allocation)
    {
        if (strategy == null)
            throw new ArgumentNullException(nameof(strategy));

        if (_strategies.TryAdd(strategy.Name, strategy))
        {
            _allocations.TryAdd(strategy.Name, allocation);

            if (allocation.IsEnabled)
            {
                await strategy.StartAsync();
                _logger.LogInformation("Registered and started strategy: {StrategyName}", strategy.Name);
            }
            else
            {
                _logger.LogInformation("Registered strategy (disabled): {StrategyName}", strategy.Name);
            }
        }
        else
        {
            _logger.LogWarning("Strategy {StrategyName} already registered", strategy.Name);
        }
    }

    /// <summary>
    /// Process market data through all active strategies
    /// </summary>
    public async Task<List<OrderIntent>> ProcessMarketDataAsync(MarketDataEvent marketEvent, OrderBook orderBook)
    {
        var allOrders = new List<OrderIntent>();

        if (!_isRunning)
            return allOrders;

        try
        {
            // Pre-flight risk checks
            if (!await _riskManager.CanTrade(marketEvent.InstrumentId))
            {
                return allOrders;
            }

            // Process through all active strategies in parallel
            var strategyTasks = new List<Task<IEnumerable<OrderIntent>>>();

            foreach (var kvp in _strategies)
            {
                var strategy = kvp.Value;
                var allocation = _allocations.GetValueOrDefault(kvp.Key);

                if (allocation?.IsEnabled == true && strategy.State == AdvancedStrategyState.Running)
                {
                    strategyTasks.Add(ProcessStrategyAsync(strategy, marketEvent, orderBook, allocation));
                }
            }

            // Wait for all strategies to complete
            var strategyResults = await Task.WhenAll(strategyTasks);

            // Combine all orders
            foreach (var orders in strategyResults)
            {
                allOrders.AddRange(orders);
            }

            // Apply portfolio-level risk management
            var filteredOrders = await _riskManager.FilterOrders(allOrders, orderBook);

            // Log activity
            if (filteredOrders.Count > 0)
            {
                Interlocked.Add(ref _totalOrdersGenerated, allOrders.Count);
                Interlocked.Add(ref _totalOrdersExecuted, filteredOrders.Count);

                _logger.LogDebug(
                    "Generated {TotalOrders} orders, executed {FilteredOrders} after risk filtering for symbol {SymbolId}",
                    allOrders.Count, filteredOrders.Count, marketEvent.InstrumentId);
            }

            return filteredOrders;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing market data for symbol {SymbolId}", marketEvent.InstrumentId);
            return allOrders;
        }
    }

    /// <summary>
    /// Process individual strategy with allocation limits
    /// </summary>
    private async Task<IEnumerable<OrderIntent>> ProcessStrategyAsync(
        IAdvancedStrategy strategy,
        MarketDataEvent marketEvent,
        OrderBook orderBook,
        StrategyAllocation allocation)
    {
        try
        {
            var orders = await strategy.ProcessMarketData(marketEvent, orderBook);

            // Apply strategy-specific position and risk limits
            var filteredOrders = ApplyStrategyLimits(orders, allocation);

            // Record strategy event for analytics
            if (filteredOrders.Any())
            {
                _eventQueue.Enqueue(new StrategyEvent
                {
                    StrategyName = strategy.Name,
                    SymbolId = marketEvent.InstrumentId,
                    EventType = StrategyEventType.OrderGenerated,
                    OrderCount = filteredOrders.Count(),
                    Timestamp = TimestampUtils.GetTimestampMicros()
                });
            }

            return filteredOrders;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing strategy {StrategyName}", strategy.Name);
            return Enumerable.Empty<OrderIntent>();
        }
    }

    /// <summary>
    /// Apply strategy-specific limits and constraints
    /// </summary>
    private List<OrderIntent> ApplyStrategyLimits(IEnumerable<OrderIntent> orders, StrategyAllocation allocation)
    {
        var filteredOrders = new List<OrderIntent>();

        foreach (var order in orders)
        {
            // Check position limits
            var currentPosition = GetCurrentPosition(allocation.StrategyName, order.SymbolId);
            var orderSize = order.Side == Side.Buy ?
                PriceTicksToDecimal(order.Quantity) :
                -PriceTicksToDecimal(order.Quantity);

            var newPosition = Math.Abs(currentPosition + orderSize);

            if (newPosition <= allocation.MaxPosition)
            {
                filteredOrders.Add(order);
            }
            else
            {
                _logger.LogWarning(
                    "Order rejected for strategy {StrategyName}: position limit exceeded. " +
                    "Current: {CurrentPosition}, New: {NewPosition}, Limit: {Limit}",
                    allocation.StrategyName, currentPosition, newPosition, allocation.MaxPosition);
            }
        }

        return filteredOrders;
    }

    /// <summary>
    /// Process strategy events for analytics and monitoring
    /// </summary>
    private async Task ProcessStrategyEvents(CancellationToken cancellationToken)
    {
        const int maxEventsPerBatch = 100;
        var processedEvents = 0;

        while (_eventQueue.TryDequeue(out var strategyEvent) && processedEvents < maxEventsPerBatch)
        {
            try
            {
                await _performanceAnalyzer.ProcessEvent(strategyEvent);
                processedEvents++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing strategy event: {Event}", strategyEvent);
            }
        }
    }

    /// <summary>
    /// Update performance metrics for all strategies
    /// </summary>
    private async Task UpdatePerformanceMetrics()
    {
        try
        {
            foreach (var kvp in _strategies)
            {
                var strategy = kvp.Value;
                var stats = strategy.GetStatistics();

                await _performanceAnalyzer.UpdateStrategyMetrics(strategy.Name, stats);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating performance metrics");
        }
    }

    /// <summary>
    /// Get comprehensive portfolio statistics
    /// </summary>
    public async Task<PortfolioStatistics> GetPortfolioStatistics()
    {
        var portfolioStats = new PortfolioStatistics
        {
            TotalStrategies = _strategies.Count,
            ActiveStrategies = _strategies.Values.Count(s => s.State == AdvancedStrategyState.Running),
            TotalOrdersGenerated = _totalOrdersGenerated,
            TotalOrdersExecuted = _totalOrdersExecuted,
            OrderExecutionRate = _totalOrdersGenerated > 0 ? (decimal)_totalOrdersExecuted / _totalOrdersGenerated : 0m,
            LastUpdateTime = TimestampUtils.GetTimestampMicros()
        };

        // Aggregate strategy statistics
        var allStats = new List<StrategyStatistics>();
        foreach (var strategy in _strategies.Values)
        {
            allStats.Add(strategy.GetStatistics());
        }

        if (allStats.Any())
        {
            portfolioStats.TotalPnL = allStats.Sum(s => s.TotalPnL);
            portfolioStats.DailyPnL = allStats.Sum(s => s.TotalPnL); // For now, use total PnL as daily
            portfolioStats.TotalValue = Math.Abs(portfolioStats.TotalPnL); // Base value calculation
            portfolioStats.OpenPositions = (int)allStats.Sum(s => s.ActivePositions);
            portfolioStats.WinRate = allStats.Average(s => s.SuccessRate) * 100; // Convert to percentage
            portfolioStats.AverageSharpe = allStats.Average(s => s.Sharpe);
            portfolioStats.MaxDrawdown = allStats.Max(s => s.MaxDrawdown);
            portfolioStats.TotalActivePositions = allStats.Sum(s => s.ActivePositions);
            portfolioStats.OverallSuccessRate = allStats.Average(s => s.SuccessRate);
        }

        // Add risk metrics
        portfolioStats.RiskMetrics = await _riskManager.GetRiskMetrics();

        // Add performance analytics
        portfolioStats.PerformanceAnalytics = await _performanceAnalyzer.GetAnalytics();

        return portfolioStats;
    }

    /// <summary>
    /// Get strategy-specific statistics
    /// </summary>
    public StrategyStatistics? GetStrategyStatistics(string strategyName)
    {
        if (_strategies.TryGetValue(strategyName, out var strategy))
        {
            return strategy.GetStatistics();
        }
        return null;
    }

    /// <summary>
    /// Enable or disable a specific strategy
    /// </summary>
    public async Task SetStrategyEnabled(string strategyName, bool enabled)
    {
        if (!_strategies.TryGetValue(strategyName, out var strategy))
        {
            throw new ArgumentException($"Strategy '{strategyName}' not found");
        }

        if (!_allocations.TryGetValue(strategyName, out var allocation))
        {
            throw new InvalidOperationException($"No allocation found for strategy '{strategyName}'");
        }

        allocation.IsEnabled = enabled;

        if (enabled && strategy.State != AdvancedStrategyState.Running)
        {
            await strategy.StartAsync();
            _logger.LogInformation("Enabled strategy: {StrategyName}", strategyName);
        }
        else if (!enabled && strategy.State == AdvancedStrategyState.Running)
        {
            await strategy.StopAsync();
            _logger.LogInformation("Disabled strategy: {StrategyName}", strategyName);
        }
    }

    /// <summary>
    /// Check if a strategy is currently enabled
    /// </summary>
    public bool IsStrategyEnabled(string strategyName)
    {
        if (_allocations.TryGetValue(strategyName, out var allocation))
        {
            return allocation.IsEnabled;
        }
        return false;
    }

    /// <summary>
    /// Get list of all available strategies
    /// </summary>
    public List<string> GetAvailableStrategies()
    {
        return _strategies.Keys.ToList();
    }

    /// <summary>
    /// Update strategy allocation
    /// </summary>
    public void UpdateStrategyAllocation(string strategyName, StrategyAllocation newAllocation)
    {
        if (_allocations.TryGetValue(strategyName, out var currentAllocation))
        {
            currentAllocation.CapitalAllocation = newAllocation.CapitalAllocation;
            currentAllocation.MaxPosition = newAllocation.MaxPosition;
            currentAllocation.RiskLimit = newAllocation.RiskLimit;

            _logger.LogInformation("Updated allocation for strategy {StrategyName}: " +
                "Capital={Capital}, MaxPosition={MaxPosition}, RiskLimit={RiskLimit}",
                strategyName, newAllocation.CapitalAllocation, newAllocation.MaxPosition, newAllocation.RiskLimit);
        }
        else
        {
            throw new ArgumentException($"Strategy '{strategyName}' not found");
        }
    }

    /// <summary>
    /// Emergency stop all strategies
    /// </summary>
    public async Task EmergencyStopAsync(string reason)
    {
        _logger.LogWarning("Emergency stop triggered: {Reason}", reason);

        _isRunning = false;

        // Stop all strategies immediately
        var stopTasks = _strategies.Values.Select(s => s.StopAsync()).ToArray();
        await Task.WhenAll(stopTasks);

        await _riskManager.EmergencyStop(reason);

        _logger.LogWarning("Emergency stop completed");
    }

    private async Task StopStrategies()
    {
        _logger.LogInformation("Stopping all strategies...");

        var stopTasks = _strategies.Values.Select(async strategy =>
        {
            try
            {
                await strategy.StopAsync();
                _logger.LogDebug("Stopped strategy: {StrategyName}", strategy.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping strategy: {StrategyName}", strategy.Name);
            }
        });

        await Task.WhenAll(stopTasks);
        _logger.LogInformation("All strategies stopped");
    }

    // Helper methods
    private decimal GetCurrentPosition(string strategyName, int symbolId)
    {
        // In a real implementation, this would query the position management system
        // For now, return 0 as a placeholder
        return 0m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private decimal PriceTicksToDecimal(long ticks) => ticks * 0.01m;

    public override void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        base.Dispose();
    }
}

// Configuration and supporting classes
public class AdvancedStrategyConfig
{
    public bool EnableArbitrage { get; set; } = true;
    public bool EnableMarketMaking { get; set; } = true;
    public bool EnableMomentum { get; set; } = true;

    public decimal ArbitrageAllocation { get; set; } = 0.3m;      // 30% allocation
    public decimal MarketMakingAllocation { get; set; } = 0.5m;  // 50% allocation
    public decimal MomentumAllocation { get; set; } = 0.2m;      // 20% allocation

    public decimal MaxArbitragePosition { get; set; } = 10m;
    public decimal MaxMarketMakingPosition { get; set; } = 50m;
    public decimal MaxMomentumPosition { get; set; } = 20m;

    public decimal ArbitrageRiskLimit { get; set; } = 0.05m;     // 5% risk limit
    public decimal MarketMakingRiskLimit { get; set; } = 0.03m; // 3% risk limit
    public decimal MomentumRiskLimit { get; set; } = 0.10m;     // 10% risk limit

    public RiskManagementConfig RiskConfig { get; set; } = new();
}

public class StrategyAllocation
{
    public required string StrategyName { get; set; }
    public decimal CapitalAllocation { get; set; }
    public decimal MaxPosition { get; set; }
    public decimal RiskLimit { get; set; }
    public bool IsEnabled { get; set; }
}

public class StrategyEvent
{
    public required string StrategyName { get; set; }
    public int SymbolId { get; set; }
    public StrategyEventType EventType { get; set; }
    public int OrderCount { get; set; }
    public long Timestamp { get; set; }
}

public enum StrategyEventType
{
    OrderGenerated,
    OrderExecuted,
    OrderRejected,
    PositionOpened,
    PositionClosed,
    RiskLimitHit
}

public class PortfolioStatistics
{
    public int TotalStrategies { get; set; }
    public int ActiveStrategies { get; set; }
    public long TotalOrdersGenerated { get; set; }
    public long TotalOrdersExecuted { get; set; }
    public decimal OrderExecutionRate { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal DailyPnL { get; set; }
    public decimal TotalValue { get; set; }
    public int OpenPositions { get; set; }
    public decimal WinRate { get; set; }
    public decimal AverageSharpe { get; set; }
    public decimal MaxDrawdown { get; set; }
    public long TotalActivePositions { get; set; }
    public decimal OverallSuccessRate { get; set; }
    public long LastUpdateTime { get; set; }
    public RiskMetrics? RiskMetrics { get; set; }
    public PerformanceAnalytics? PerformanceAnalytics { get; set; }
}

public interface IAdvancedStrategyManager
{
    Task RegisterStrategy(IAdvancedStrategy strategy, StrategyAllocation allocation);
    Task<List<OrderIntent>> ProcessMarketDataAsync(MarketDataEvent marketEvent, OrderBook orderBook);
    Task<PortfolioStatistics> GetPortfolioStatistics();
    StrategyStatistics? GetStrategyStatistics(string strategyName);
    Task SetStrategyEnabled(string strategyName, bool enabled);
    bool IsStrategyEnabled(string strategyName);
    List<string> GetAvailableStrategies();
    void UpdateStrategyAllocation(string strategyName, StrategyAllocation newAllocation);
    Task EmergencyStopAsync(string reason);
}
