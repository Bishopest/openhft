using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Book.Models;
using OpenHFT.Book.Core;
using System.Collections.Concurrent;

namespace OpenHFT.Strategy.Advanced;

/// <summary>
/// Advanced risk management system for HFT strategies with real-time monitoring,
/// position limits, drawdown controls, and automated risk responses.
/// </summary>
public class RiskManager
{
    private readonly ILogger _logger;
    private readonly RiskManagementConfig _config;
    private readonly ConcurrentDictionary<int, SymbolRiskState> _symbolRisk;
    private readonly ConcurrentDictionary<string, StrategyRiskState> _strategyRisk;
    
    // Global risk state
    private decimal _currentDrawdown;
    private decimal _maxDrawdown;
    private decimal _totalPnL;
    private long _totalVolume;
    private readonly object _globalRiskLock = new();
    
    // Circuit breaker
    private bool _circuitBreakerTriggered;
    private DateTime _circuitBreakerTime;
    private int _consecutiveRiskEvents;
    
    public RiskManager(ILogger logger, RiskManagementConfig config)
    {
        _logger = logger;
        _config = config;
        _symbolRisk = new ConcurrentDictionary<int, SymbolRiskState>();
        _strategyRisk = new ConcurrentDictionary<string, StrategyRiskState>();
    }
    
    public Task StartAsync()
    {
        _logger.LogInformation("Risk Manager started with max drawdown: {MaxDrawdown:P2}", _config.MaxDrawdown);
        return Task.CompletedTask;
    }
    
    public Task StopAsync()
    {
        _logger.LogInformation("Risk Manager stopped");
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Check if trading is allowed for a specific symbol
    /// </summary>
    public async Task<bool> CanTrade(int symbolId)
    {
        // Check global circuit breaker
        if (_circuitBreakerTriggered)
        {
            var timeSinceBreaker = DateTime.UtcNow - _circuitBreakerTime;
            if (timeSinceBreaker < _config.CircuitBreakerCooldown)
            {
                return false;
            }
            else
            {
                // Reset circuit breaker
                _circuitBreakerTriggered = false;
                _consecutiveRiskEvents = 0;
                _logger.LogInformation("Circuit breaker reset after cooldown period");
            }
        }
        
        // Check global drawdown limit
        lock (_globalRiskLock)
        {
            if (_currentDrawdown > _config.MaxDrawdown)
            {
                TriggerCircuitBreaker("Global drawdown limit exceeded");
                return false;
            }
        }
        
        // Check symbol-specific risk
        var symbolRisk = GetOrCreateSymbolRisk(symbolId);
        if (symbolRisk.IsBlocked)
        {
            return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Filter orders based on risk constraints
    /// </summary>
    public async Task<List<OrderIntent>> FilterOrders(List<OrderIntent> orders, OrderBook orderBook)
    {
        var filteredOrders = new List<OrderIntent>();
        
        foreach (var order in orders)
        {
            if (await ValidateOrder(order, orderBook))
            {
                filteredOrders.Add(order);
            }
        }
        
        return filteredOrders;
    }
    
    /// <summary>
    /// Validate individual order against risk limits
    /// </summary>
    private async Task<bool> ValidateOrder(OrderIntent order, OrderBook orderBook)
    {
        try
        {
            // 1. Basic sanity checks
            if (!ValidateBasicOrder(order))
                return false;
            
            // 2. Position size limits
            if (!ValidatePositionSize(order))
                return false;
            
            // 3. Concentration limits
            if (!ValidateConcentration(order))
                return false;
            
            // 4. Volatility-based limits
            if (!ValidateVolatilityLimits(order, orderBook))
                return false;
            
            // 5. Liquidity checks
            if (!ValidateLiquidity(order, orderBook))
                return false;
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating order: {Order}", order);
            return false;
        }
    }
    
    private bool ValidateBasicOrder(OrderIntent order)
    {
        // Check minimum/maximum order size
        var orderSize = PriceTicksToDecimal(order.Quantity);
        
        if (orderSize < _config.MinOrderSize || orderSize > _config.MaxOrderSize)
        {
            _logger.LogWarning("Order rejected: size {Size} outside limits [{Min}, {Max}]",
                orderSize, _config.MinOrderSize, _config.MaxOrderSize);
            return false;
        }
        
        // Check price reasonableness (prevent fat finger errors)
        if (order.Type == OrderType.Limit)
        {
            var price = PriceTicksToDecimal(order.PriceTicks);
            if (price <= 0 || price > _config.MaxOrderPrice)
            {
                _logger.LogWarning("Order rejected: invalid price {Price}", price);
                return false;
            }
        }
        
        return true;
    }
    
    private bool ValidatePositionSize(OrderIntent order)
    {
        var symbolRisk = GetOrCreateSymbolRisk(order.SymbolId);
        var orderSize = order.Side == Side.Buy ? 
            PriceTicksToDecimal(order.Quantity) : 
            -PriceTicksToDecimal(order.Quantity);
        
        var newPosition = Math.Abs(symbolRisk.CurrentPosition + orderSize);
        
        if (newPosition > _config.MaxPositionPerSymbol)
        {
            _logger.LogWarning("Order rejected: position limit exceeded for symbol {SymbolId}. " +
                "Current: {Current}, New: {New}, Limit: {Limit}",
                order.SymbolId, symbolRisk.CurrentPosition, newPosition, _config.MaxPositionPerSymbol);
            return false;
        }
        
        return true;
    }
    
    private bool ValidateConcentration(OrderIntent order)
    {
        // Check if adding this order would create excessive concentration in a single symbol
        var orderValue = CalculateOrderValue(order);
        var totalPortfolioValue = CalculateTotalPortfolioValue();
        
        if (totalPortfolioValue > 0)
        {
            var symbolRisk = GetOrCreateSymbolRisk(order.SymbolId);
            var newSymbolExposure = symbolRisk.TotalExposure + orderValue;
            var concentrationRatio = newSymbolExposure / totalPortfolioValue;
            
            if (concentrationRatio > _config.MaxConcentrationPerSymbol)
            {
                _logger.LogWarning("Order rejected: concentration limit exceeded for symbol {SymbolId}. " +
                    "Ratio: {Ratio:P2}, Limit: {Limit:P2}",
                    order.SymbolId, concentrationRatio, _config.MaxConcentrationPerSymbol);
                return false;
            }
        }
        
        return true;
    }
    
    private bool ValidateVolatilityLimits(OrderIntent order, OrderBook orderBook)
    {
        var symbolRisk = GetOrCreateSymbolRisk(order.SymbolId);
        
        // If volatility is too high, reduce allowed position sizes
        if (symbolRisk.RecentVolatility > _config.HighVolatilityThreshold)
        {
            var volatilityAdjustment = _config.VolatilityAdjustmentFactor;
            var adjustedMaxPosition = _config.MaxPositionPerSymbol * volatilityAdjustment;
            
            var orderSize = PriceTicksToDecimal(order.Quantity);
            if (Math.Abs(symbolRisk.CurrentPosition + orderSize) > adjustedMaxPosition)
            {
                _logger.LogWarning("Order rejected: volatility-adjusted position limit exceeded for symbol {SymbolId}. " +
                    "Volatility: {Volatility:P4}, Adjusted Limit: {Limit:F2}",
                    order.SymbolId, symbolRisk.RecentVolatility, adjustedMaxPosition);
                return false;
            }
        }
        
        return true;
    }
    
    private bool ValidateLiquidity(OrderIntent order, OrderBook orderBook)
    {
        // Don't allow orders that are too large relative to available liquidity
        var orderSize = PriceTicksToDecimal(order.Quantity);
        var availableLiquidity = CalculateAvailableLiquidity(orderBook, order.Side);
        
        var liquidityRatio = orderSize / Math.Max(availableLiquidity, 0.001m);
        
        if (liquidityRatio > _config.MaxLiquidityUtilization)
        {
            _logger.LogWarning("Order rejected: liquidity utilization too high for symbol {SymbolId}. " +
                "Ratio: {Ratio:P2}, Limit: {Limit:P2}",
                order.SymbolId, liquidityRatio, _config.MaxLiquidityUtilization);
            return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Continuous risk monitoring
    /// </summary>
    public async Task MonitorRisk()
    {
        try
        {
            // Update global risk metrics
            UpdateGlobalRiskMetrics();
            
            // Check for risk limit violations
            await CheckRiskLimitViolations();
            
            // Update symbol risk states
            UpdateSymbolRiskStates();
            
            // Auto-adjust parameters based on market conditions
            AutoAdjustRiskParameters();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in risk monitoring");
        }
    }
    
    private void UpdateGlobalRiskMetrics()
    {
        lock (_globalRiskLock)
        {
            // Calculate current drawdown
            var highWaterMark = _totalPnL; // Simplified - in reality, track historical high
            _currentDrawdown = Math.Max(0, highWaterMark - _totalPnL) / Math.Max(Math.Abs(highWaterMark), 1m);
            _maxDrawdown = Math.Max(_maxDrawdown, _currentDrawdown);
        }
    }
    
    private async Task CheckRiskLimitViolations()
    {
        // Check if any strategy has violated risk limits
        foreach (var kvp in _strategyRisk)
        {
            var strategy = kvp.Key;
            var riskState = kvp.Value;
            
            if (riskState.CurrentDrawdown > riskState.MaxAllowedDrawdown)
            {
                await HandleRiskViolation($"Strategy {strategy} exceeded drawdown limit", strategy);
            }
            
            if (riskState.DailyLoss > riskState.MaxDailyLoss)
            {
                await HandleRiskViolation($"Strategy {strategy} exceeded daily loss limit", strategy);
            }
        }
    }
    
    private async Task HandleRiskViolation(string reason, string? strategyName = null)
    {
        _consecutiveRiskEvents++;
        
        _logger.LogWarning("Risk violation detected: {Reason}. Consecutive events: {Count}",
            reason, _consecutiveRiskEvents);
        
        // If too many consecutive risk events, trigger circuit breaker
        if (_consecutiveRiskEvents >= _config.MaxConsecutiveRiskEvents)
        {
            TriggerCircuitBreaker($"Too many consecutive risk events: {reason}");
        }
        
        // Strategy-specific actions
        if (strategyName != null && _strategyRisk.TryGetValue(strategyName, out var riskState))
        {
            riskState.IsBlocked = true;
            riskState.BlockedUntil = DateTime.UtcNow.Add(_config.StrategyBlockDuration);
            
            _logger.LogWarning("Blocked strategy {StrategyName} until {Until}",
                strategyName, riskState.BlockedUntil);
        }
    }
    
    private void TriggerCircuitBreaker(string reason)
    {
        _circuitBreakerTriggered = true;
        _circuitBreakerTime = DateTime.UtcNow;
        
        _logger.LogError("Circuit breaker triggered: {Reason}. Trading halted until {Until}",
            reason, _circuitBreakerTime.Add(_config.CircuitBreakerCooldown));
    }
    
    private void UpdateSymbolRiskStates()
    {
        foreach (var kvp in _symbolRisk)
        {
            var symbolId = kvp.Key;
            var riskState = kvp.Value;
            
            // Update volatility estimates
            if (riskState.PriceHistory.Count > 10)
            {
                riskState.RecentVolatility = CalculateVolatility(riskState.PriceHistory.TakeLast(20).ToList());
            }
            
            // Check if symbol should be unblocked
            if (riskState.IsBlocked && DateTime.UtcNow > riskState.BlockedUntil)
            {
                riskState.IsBlocked = false;
                _logger.LogInformation("Unblocked symbol {SymbolId}", symbolId);
            }
        }
    }
    
    private void AutoAdjustRiskParameters()
    {
        // Automatically adjust risk parameters based on market volatility
        var avgVolatility = _symbolRisk.Values
            .Where(s => s.RecentVolatility > 0)
            .Select(s => s.RecentVolatility)
            .DefaultIfEmpty(0.02m)
            .Average();
        
        // Increase position limits in low volatility environments
        if (avgVolatility < 0.01m) // Low volatility
        {
            // Could adjust _config.MaxPositionPerSymbol upward
        }
        else if (avgVolatility > 0.05m) // High volatility
        {
            // Could adjust _config.MaxPositionPerSymbol downward
        }
    }
    
    /// <summary>
    /// Emergency stop all trading
    /// </summary>
    public async Task EmergencyStop(string reason)
    {
        _logger.LogCritical("EMERGENCY STOP triggered: {Reason}", reason);
        
        TriggerCircuitBreaker($"Emergency stop: {reason}");
        
        // Block all symbols and strategies
        foreach (var symbolRisk in _symbolRisk.Values)
        {
            symbolRisk.IsBlocked = true;
            symbolRisk.BlockedUntil = DateTime.UtcNow.AddHours(24); // 24-hour block
        }
        
        foreach (var strategyRisk in _strategyRisk.Values)
        {
            strategyRisk.IsBlocked = true;
            strategyRisk.BlockedUntil = DateTime.UtcNow.AddHours(24);
        }
    }
    
    /// <summary>
    /// Get current risk metrics for monitoring
    /// </summary>
    public async Task<RiskMetrics> GetRiskMetrics()
    {
        lock (_globalRiskLock)
        {
            return new RiskMetrics
            {
                CurrentDrawdown = _currentDrawdown,
                MaxDrawdown = _maxDrawdown,
                TotalPnL = _totalPnL,
                CircuitBreakerActive = _circuitBreakerTriggered,
                CircuitBreakerTime = _circuitBreakerTime,
                ConsecutiveRiskEvents = _consecutiveRiskEvents,
                BlockedSymbols = _symbolRisk.Values.Count(s => s.IsBlocked),
                BlockedStrategies = _strategyRisk.Values.Count(s => s.IsBlocked),
                AverageVolatility = _symbolRisk.Values
                    .Where(s => s.RecentVolatility > 0)
                    .Select(s => s.RecentVolatility)
                    .DefaultIfEmpty(0m)
                    .Average(),
                TotalVolume = _totalVolume,
                LastUpdateTime = TimestampUtils.GetTimestampMicros()
            };
        }
    }
    
    // Helper methods
    private SymbolRiskState GetOrCreateSymbolRisk(int symbolId)
    {
        return _symbolRisk.GetOrAdd(symbolId, _ => new SymbolRiskState(symbolId));
    }
    
    private StrategyRiskState GetOrCreateStrategyRisk(string strategyName)
    {
        return _strategyRisk.GetOrAdd(strategyName, _ => new StrategyRiskState(strategyName));
    }
    
    private decimal CalculateOrderValue(OrderIntent order)
    {
        var price = order.Type == OrderType.Market ? 100m : PriceTicksToDecimal(order.PriceTicks); // Estimate for market orders
        var quantity = PriceTicksToDecimal(order.Quantity);
        return price * quantity;
    }
    
    private decimal CalculateTotalPortfolioValue()
    {
        return _symbolRisk.Values.Sum(s => s.TotalExposure);
    }
    
    private decimal CalculateAvailableLiquidity(OrderBook orderBook, Side side)
    {
        decimal totalLiquidity = 0m;
        // For buying we need ask liquidity, for selling we need bid liquidity
        var targetSide = side == Side.Buy ? Side.Sell : Side.Buy;
        var bookLevels = orderBook.GetTopLevels(targetSide, 5).ToArray();
        
        for (int i = 0; i < bookLevels.Length; i++)
        {
            var level = bookLevels[i];
            if (level != null && !level.IsEmpty)
            {
                totalLiquidity += PriceTicksToDecimal(level.TotalQuantity);
            }
        }
        
        return totalLiquidity;
    }
    
    private decimal CalculateVolatility(List<decimal> prices)
    {
        if (prices.Count < 2) return 0m;
        
        var returns = new List<decimal>();
        for (int i = 1; i < prices.Count; i++)
        {
            if (prices[i - 1] > 0)
            {
                returns.Add((prices[i] / prices[i - 1]) - 1m);
            }
        }
        
        if (returns.Count == 0) return 0m;
        
        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / returns.Count;
        return (decimal)Math.Sqrt((double)variance);
    }
    
    private decimal PriceTicksToDecimal(long ticks) => ticks * 0.01m;
}

// Supporting classes and enums
public class RiskManagementConfig
{
    public decimal MaxDrawdown { get; set; } = 0.10m;                    // 10% max drawdown
    public decimal MaxPositionPerSymbol { get; set; } = 100m;            // Max position per symbol
    public decimal MaxOrderSize { get; set; } = 50m;                     // Max single order size
    public decimal MinOrderSize { get; set; } = 0.01m;                   // Min single order size
    public decimal MaxOrderPrice { get; set; } = 1_000_000m;             // Max order price (prevent fat fingers)
    public decimal MaxConcentrationPerSymbol { get; set; } = 0.25m;      // 25% max concentration per symbol
    public decimal MaxLiquidityUtilization { get; set; } = 0.30m;        // 30% max liquidity utilization
    public decimal HighVolatilityThreshold { get; set; } = 0.03m;        // 3% volatility threshold
    public decimal VolatilityAdjustmentFactor { get; set; } = 0.5m;      // 50% position reduction in high vol
    public int MaxConsecutiveRiskEvents { get; set; } = 3;               // Max consecutive risk events before circuit breaker
    public TimeSpan CircuitBreakerCooldown { get; set; } = TimeSpan.FromMinutes(15); // Circuit breaker cooldown
    public TimeSpan StrategyBlockDuration { get; set; } = TimeSpan.FromMinutes(5);   // Strategy block duration
}

public class SymbolRiskState
{
    public int SymbolId { get; }
    public decimal CurrentPosition { get; set; }
    public decimal TotalExposure { get; set; }
    public decimal RecentVolatility { get; set; }
    public bool IsBlocked { get; set; }
    public DateTime BlockedUntil { get; set; }
    public List<decimal> PriceHistory { get; } = new();
    
    public SymbolRiskState(int symbolId)
    {
        SymbolId = symbolId;
    }
}

public class StrategyRiskState
{
    public string StrategyName { get; }
    public decimal CurrentDrawdown { get; set; }
    public decimal MaxAllowedDrawdown { get; set; } = 0.05m; // 5% max drawdown per strategy
    public decimal DailyLoss { get; set; }
    public decimal MaxDailyLoss { get; set; } = 1000m; // $1000 max daily loss per strategy
    public bool IsBlocked { get; set; }
    public DateTime BlockedUntil { get; set; }
    
    public StrategyRiskState(string strategyName)
    {
        StrategyName = strategyName;
    }
}

public class RiskMetrics
{
    public decimal CurrentDrawdown { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal TotalPnL { get; set; }
    public bool CircuitBreakerActive { get; set; }
    public DateTime CircuitBreakerTime { get; set; }
    public int ConsecutiveRiskEvents { get; set; }
    public int BlockedSymbols { get; set; }
    public int BlockedStrategies { get; set; }
    public decimal AverageVolatility { get; set; }
    public long TotalVolume { get; set; }
    public long LastUpdateTime { get; set; }
}
