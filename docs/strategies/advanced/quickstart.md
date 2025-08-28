# Quick Start Guide - Advanced Strategies

## 🚀 Quick Setup (5 minutes)

### 1. Build and Run
```bash
cd src/OpenHFT.Strategy.Advanced
dotnet build
dotnet run
```

### 2. Basic Configuration
```csharp
var config = new AdvancedStrategyConfig
{
    EnableArbitrage = true,        // Enable triangular arbitrage
    EnableMarketMaking = true,     // Enable market making
    EnableMomentum = true,         // Enable ML momentum
    
    ArbitrageAllocation = 0.3m,    // 30% of capital
    MarketMakingAllocation = 0.5m, // 50% of capital  
    MomentumAllocation = 0.2m      // 20% of capital
};
```

## 📊 Key Components

### Strategy Manager
- **Purpose**: Orchestrates all strategies
- **Key Method**: `ProcessMarketDataAsync()`
- **Returns**: List of `OrderIntent` objects

### Risk Manager  
- **Purpose**: Validates orders and monitors risk
- **Key Limits**: Position size, drawdown, volatility
- **Emergency**: Automatic stop-loss at 5% drawdown

### Performance Analyzer
- **Purpose**: Tracks performance metrics
- **Key Metrics**: Sharpe ratio, win rate, max drawdown
- **Updates**: Real-time performance calculation

## 🎯 Strategy Overview

### Triangular Arbitrage
```
Monitors: BTC/USDT, ETH/USDT, BTC/ETH
Opportunity: When BTC/USDT ÷ ETH/USDT ≠ BTC/ETH
Execution: Simultaneous 3-leg trades
Profit: Risk-free spread capture
```

### Market Making
```
Action: Provides liquidity via bid/ask quotes
Profit: Bid-ask spread capture
Risk: Inventory management
Adjustment: Dynamic spreads based on volatility
```

### ML Momentum
```
Input: 20+ technical indicators
Model: Linear regression with momentum features  
Signal: Buy/sell based on predicted price direction
Risk: Stop-loss and position sizing
```

## 🔧 Common Tasks

### Add New Strategy
```csharp
// 1. Implement interface
public class MyStrategy : IAdvancedStrategy
{
    public async Task<List<OrderIntent>> ProcessMarketData(
        MarketDataEvent marketData, OrderBook orderBook)
    {
        // Your strategy logic here
        return new List<OrderIntent>();
    }
}

// 2. Register in DI
services.AddTransient<MyStrategy>();

// 3. Add to manager
await strategyManager.RegisterStrategy(myStrategy, allocation);
```

### Modify Risk Limits
```csharp
var config = new AdvancedStrategyConfig
{
    MaxArbitragePosition = 20m,     // Increase from 10m
    ArbitrageRiskLimit = 0.03m,     // Reduce from 0.05m  
    MaxDrawdown = 0.08m             // Increase tolerance
};
```

### Custom Performance Metrics
```csharp
var customMetrics = new Dictionary<string, object>
{
    ["MyCustomMetric"] = CalculateCustomValue(),
    ["StrategySpecificKPI"] = GetKPIValue()
};

strategyStats.StrategySpecificMetrics = customMetrics;
```

## 📈 Performance Targets

| Metric | Target Range | Excellent |
|--------|-------------|-----------|
| Sharpe Ratio | 1.0 - 2.0 | > 2.0 |
| Max Drawdown | < 5% | < 3% |
| Win Rate | 60% - 75% | > 75% |
| Order Fill Rate | > 90% | > 95% |
| Latency | < 1ms | < 100μs |

## 🚨 Troubleshooting

### Build Issues
```bash
# Clear build cache
dotnet clean
dotnet restore
dotnet build
```

### Runtime Errors
- Check dependency injection configuration
- Verify all required packages are installed
- Ensure order book data is valid

### Performance Issues  
- Enable detailed logging
- Check memory usage with profiler
- Verify threading configuration

### Strategy Not Generating Orders
- Check position limits in config
- Verify risk manager settings
- Review market data quality

## 🎯 Best Practices

### Performance
- Use structs for high-frequency data
- Pre-allocate arrays and collections
- Minimize garbage collection pressure
- Use object pooling for frequent allocations

### Risk Management
- Always validate orders through risk manager
- Set conservative position limits initially
- Monitor correlation between strategies
- Implement emergency stop procedures

### Monitoring
- Log key performance metrics
- Track latency percentiles
- Monitor memory usage
- Alert on abnormal behavior

### Testing
- Unit test individual strategies
- Integration test with real market data
- Stress test with high-frequency events
- Validate risk controls under extreme conditions

## 📚 Key Files

| File | Purpose |
|------|---------|
| `Program.cs` | Main demo and DI setup |
| `AdvancedStrategyManager.cs` | Central orchestration |
| `RiskManager.cs` | Risk controls and limits |
| `PerformanceAnalyzer.cs` | Metrics and analytics |
| `TriangularArbitrageStrategy.cs` | Arbitrage implementation |
| `OptimalMarketMakingStrategy.cs` | Market making logic |
| `MLMomentumStrategy.cs` | ML momentum strategy |

## 🔗 Integration Points

### With Core System
- Uses `MarketDataEvent` from OpenHFT.Core
- Generates `OrderIntent` objects
- Integrates with `OrderBook` from OpenHFT.Book

### With UI Dashboard  
- Provides real-time statistics
- Exports performance metrics
- Supports strategy enable/disable

### With Risk System
- All orders validated by `RiskManager`
- Real-time position monitoring
- Automatic risk limit enforcement

## 📊 Sample Output

```
🚀 OpenHFT Advanced Strategy Demonstration
==========================================

📊 Processing Market Data Events:
Event 01: Trade | BTC/USDT | $43289.14 | Vol: 381814.59
         📝 No orders generated
Event 02: Update | ETH/USDT | $2744.29 | Vol: 3625926.75
         📝 No orders generated

📈 Strategy Performance Summary
==============================
Total Strategies: 3
Active Strategies: 3
Total PnL: $1,247.83
Average Sharpe: 1.847
Success Rate: 67.45%
Max Drawdown: 2.31%
Active Positions: 12
Order Execution Rate: 94.23%

✅ Advanced Strategy Demonstration Complete!
```

## ⚡ Quick Commands

```bash
# Build project
dotnet build

# Run with verbose logging
dotnet run --verbosity detailed

# Clean and rebuild
dotnet clean && dotnet build

# Run tests
dotnet test

# Check project info
dotnet list package
```

## 🎯 Configuration Shortcuts

### Conservative Setup (Low Risk)
```csharp
var config = new AdvancedStrategyConfig
{
    ArbitrageAllocation = 0.5m,      // 50% in low-risk arbitrage
    MarketMakingAllocation = 0.4m,   // 40% in market making
    MomentumAllocation = 0.1m,       // 10% in momentum
    MaxDrawdown = 0.02m              // 2% max drawdown
};
```

### Aggressive Setup (Higher Risk/Reward)
```csharp
var config = new AdvancedStrategyConfig
{
    ArbitrageAllocation = 0.2m,      // 20% in arbitrage
    MarketMakingAllocation = 0.3m,   // 30% in market making
    MomentumAllocation = 0.5m,       // 50% in momentum
    MaxDrawdown = 0.08m              // 8% max drawdown
};
```

### Development/Testing Setup
```csharp
var config = new AdvancedStrategyConfig
{
    EnableArbitrage = true,
    EnableMarketMaking = false,      // Disable for isolated testing
    EnableMomentum = false,          // Test one strategy at a time
    MaxPositionSize = 1m,            // Small positions
    MaxDrawdown = 0.01m              // Very low risk
};
```

---

This quick start guide gets you up and running with the Advanced Strategy module in minutes. For detailed technical information, see the complete documentation in the [strategies folder](../../strategies/).
