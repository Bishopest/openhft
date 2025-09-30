using OpenHFT.Core.Models;
using OpenHFT.Book.Models;
using OpenHFT.Book.Core;
using OpenHFT.Strategy.Interfaces;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using OpenHFT.Core.Utils;
using System.Collections.Generic;

namespace OpenHFT.Strategy.Advanced.Arbitrage;

/// <summary>
/// Triangular arbitrage strategy that detects profitable cycles across 3 currency pairs
/// Example: BTC/USDT → ETH/BTC → ETH/USDT → USDT
/// Ultra-fast detection and execution required for HFT profitability
/// </summary>
public class TriangularArbitrageStrategy : IAdvancedStrategy
{
    private readonly ILogger<TriangularArbitrageStrategy> _logger;
    private readonly Dictionary<string, OrderBook> _orderBooks;
    private readonly Dictionary<string, decimal> _lastPrices;

    // Arbitrage configuration
    private readonly decimal _minProfitThreshold = 0.00001m; // 0.001% minimum profit (lowered for testing)
    private readonly decimal _maxPositionSize = 1000m;     // Max position per leg
    private readonly TimeSpan _maxExecutionWindow = TimeSpan.FromMilliseconds(50); // 50ms max execution

    // Performance tracking
    private long _opportunitiesDetected;
    private long _opportunitiesExecuted;
    private decimal _totalProfitRealized;
    private long _executionCount;
    private readonly object _statsLock = new();

    public TriangularArbitrageStrategy(ILogger<TriangularArbitrageStrategy> logger)
    {
        _logger = logger;
        _orderBooks = new Dictionary<string, OrderBook>();
        _lastPrices = new Dictionary<string, decimal>();
    }

    public string Name => "TriangularArbitrage";
    public AdvancedStrategyState State { get; private set; } = AdvancedStrategyState.Stopped;

    /// <summary>
    /// Process market data and detect triangular arbitrage opportunities
    /// </summary>
    public async Task<List<OrderIntent>> ProcessMarketData(MarketDataEvent marketData, OrderBook orderBook)
    {
        var symbol = GetSymbolName(marketData.InstrumentId);

        // Update order book reference
        _orderBooks[symbol] = orderBook;
        _lastPrices[symbol] = PriceTicksToDecimal(marketData.PriceTicks);

        // Accept trade events and update events for testing (normally would only use Trade)
        if (marketData.Kind != EventKind.Trade && marketData.Kind != EventKind.Update)
            return new List<OrderIntent>();

        // Increment execution counter
        Interlocked.Increment(ref _executionCount);

        // Log every 100 executions to show activity
        if (_executionCount % 100 == 0)
        {
            _logger.LogInformation("TriangularArbitrage processed {Count} market events for {Symbol}",
                _executionCount, symbol);
        }

        // TEMPORARY: Generate a test order every 200 executions to verify the pipeline
        var orders = new List<OrderIntent>();
        if (_executionCount % 200 == 0)
        {
            var bestPrice = GetBestPrice(orderBook);
            if (bestPrice != null)
            {
                var testOrder = new OrderIntent(
                    clientOrderId: GenerateOrderId(), // Use the existing method that returns long
                    type: OrderType.Limit,
                    side: Side.Buy,
                    priceTicks: DecimalToPriceTicks(bestPrice.Value.bid * 0.999m), // 0.1% below bid
                    quantity: DecimalToQuantityTicks(0.001m), // Very small quantity
                    timestampIn: TimestampUtils.GetTimestampMicros(),
                    symbolId: marketData.InstrumentId
                );

                orders.Add(testOrder);

                _logger.LogInformation(
                    "TriangularArbitrage generated TEST order: {Side} {Quantity} at {Price} for symbol {SymbolId}",
                    testOrder.Side, PriceTicksToDecimal(testOrder.Quantity),
                    PriceTicksToDecimal(testOrder.PriceTicks), marketData.InstrumentId);
            }
        }

        // Original arbitrage detection (keeping it for real opportunities)
        var opportunities = DetectArbitrageOpportunities();

        if (opportunities.Any())
        {
            _logger.LogInformation("Found {Count} arbitrage opportunities", opportunities.Count);

            foreach (var opportunity in opportunities)
            {
                var executionOrders = await ExecuteArbitrageOpportunity(opportunity);
                orders.AddRange(executionOrders);
            }
        }

        return orders;
    }

    /// <summary>
    /// Detect all possible triangular arbitrage cycles
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private List<ArbitrageOpportunity> DetectArbitrageOpportunities()
    {
        var opportunities = new List<ArbitrageOpportunity>();

        // Check common triangular cycles
        opportunities.AddRange(CheckTriangularCycle("BTCUSDT", "ETHBTC", "ETHUSDT"));
        opportunities.AddRange(CheckTriangularCycle("BTCUSDT", "ADABTC", "ADAUSDT"));
        opportunities.AddRange(CheckTriangularCycle("ETHUSDT", "ADAETH", "ADAUSDT"));
        opportunities.AddRange(CheckTriangularCycle("BTCUSDT", "BNBBTC", "BNBUSDT"));

        return opportunities.Where(op => op.ProfitPercentage >= _minProfitThreshold).ToList();
    }

    /// <summary>
    /// Check specific triangular cycle for arbitrage opportunity
    /// </summary>
    private List<ArbitrageOpportunity> CheckTriangularCycle(string pairA, string pairB, string pairC)
    {
        var opportunities = new List<ArbitrageOpportunity>();

        if (!_orderBooks.TryGetValue(pairA, out var bookA) ||
            !_orderBooks.TryGetValue(pairB, out var bookB) ||
            !_orderBooks.TryGetValue(pairC, out var bookC))
        {
            return opportunities; // Missing order book data
        }

        // Log strategy execution every 100 calls
        if (_executionCount % 100 == 0)
        {
            _logger.LogInformation("TriangularArbitrage: Analyzing cycle {PairA}-{PairB}-{PairC} (Execution #{Count})",
                pairA, pairB, pairC, _executionCount);
        }

        // Get best prices
        var priceA = GetBestPrice(bookA);
        var priceB = GetBestPrice(bookB);
        var priceC = GetBestPrice(bookC);

        if (priceA == null || priceB == null || priceC == null)
            return opportunities;

        // Calculate both direction opportunities
        // Direction 1: A → B → C → A
        var opportunity1 = CalculateArbitrageProfit(
            pairA, pairB, pairC,
            priceA.Value.ask, priceB.Value.bid, priceC.Value.bid,
            ArbitrageDirection.Forward);

        if (opportunity1 != null)
            opportunities.Add(opportunity1);

        // Direction 2: A ← B ← C ← A (reverse)
        var opportunity2 = CalculateArbitrageProfit(
            pairA, pairB, pairC,
            priceA.Value.bid, priceB.Value.ask, priceC.Value.ask,
            ArbitrageDirection.Reverse);

        if (opportunity2 != null)
            opportunities.Add(opportunity2);

        return opportunities;
    }

    /// <summary>
    /// Calculate arbitrage profit for given price path
    /// </summary>
    private ArbitrageOpportunity? CalculateArbitrageProfit(
        string pairA, string pairB, string pairC,
        decimal priceA, decimal priceB, decimal priceC,
        ArbitrageDirection direction)
    {
        try
        {
            decimal startAmount = 1000m; // Start with 1000 USDT equivalent
            decimal currentAmount = startAmount;

            // Execute the triangular path
            if (direction == ArbitrageDirection.Forward)
            {
                // Buy A with USDT
                currentAmount = currentAmount / priceA;

                // Sell A for B
                currentAmount = currentAmount * priceB;

                // Sell B for USDT
                currentAmount = currentAmount * priceC;
            }
            else
            {
                // Reverse path
                currentAmount = currentAmount / priceC;
                currentAmount = currentAmount / priceB;
                currentAmount = currentAmount * priceA;
            }

            var profit = currentAmount - startAmount;
            var profitPercentage = profit / startAmount;

            if (profitPercentage >= _minProfitThreshold)
            {
                Interlocked.Increment(ref _opportunitiesDetected);

                return new ArbitrageOpportunity
                {
                    PairA = pairA,
                    PairB = pairB,
                    PairC = pairC,
                    Direction = direction,
                    ProfitAmount = profit,
                    ProfitPercentage = profitPercentage,
                    StartAmount = startAmount,
                    ExpectedEndAmount = currentAmount,
                    DetectedAt = TimestampUtils.GetTimestampMicros(),
                    PriceA = priceA,
                    PriceB = priceB,
                    PriceC = priceC
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating arbitrage profit for {PairA}-{PairB}-{PairC}",
                pairA, pairB, pairC);
        }

        return null;
    }

    /// <summary>
    /// Execute arbitrage opportunity with multiple simultaneous orders
    /// </summary>
    private async Task<List<OrderIntent>> ExecuteArbitrageOpportunity(ArbitrageOpportunity opportunity)
    {
        var startTime = TimestampUtils.GetTimestampMicros();
        var orders = new List<OrderIntent>();

        try
        {
            // Calculate optimal position sizes based on available liquidity
            var positionSize = CalculateOptimalPositionSize(opportunity);

            if (positionSize < 0.001m) // Minimum viable size
                return orders;

            // Generate simultaneous orders for all legs
            var leg1Order = CreateArbitrageLegOrder(opportunity.PairA, opportunity.Direction, positionSize, 1);
            var leg2Order = CreateArbitrageLegOrder(opportunity.PairB, opportunity.Direction, positionSize, 2);
            var leg3Order = CreateArbitrageLegOrder(opportunity.PairC, opportunity.Direction, positionSize, 3);

            // Add only non-null orders
            var legOrders = new[] { leg1Order, leg2Order, leg3Order }.Where(o => o != null).Cast<OrderIntent>();
            orders.AddRange(legOrders);

            // Log execution
            Interlocked.Increment(ref _opportunitiesExecuted);

            var executionTime = TimestampUtils.GetTimestampMicros() - startTime;

            _logger.LogInformation(
                "Executing triangular arbitrage: {PairA}-{PairB}-{PairC} | " +
                "Profit: {Profit:P4} | Size: {Size:F2} | Time: {Time}μs",
                opportunity.PairA, opportunity.PairB, opportunity.PairC,
                opportunity.ProfitPercentage, positionSize, executionTime);

            // Update stats
            lock (_statsLock)
            {
                _totalProfitRealized += opportunity.ProfitAmount * positionSize / opportunity.StartAmount;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute arbitrage opportunity: {Opportunity}", opportunity);
        }

        return orders;
    }

    /// <summary>
    /// Calculate optimal position size based on order book liquidity
    /// </summary>
    private decimal CalculateOptimalPositionSize(ArbitrageOpportunity opportunity)
    {
        // Get available liquidity at best prices for all legs
        var liquidityA = GetAvailableLiquidity(opportunity.PairA, opportunity.Direction);
        var liquidityB = GetAvailableLiquidity(opportunity.PairB, opportunity.Direction);
        var liquidityC = GetAvailableLiquidity(opportunity.PairC, opportunity.Direction);

        // Position size limited by minimum liquidity and max position setting
        var maxByLiquidity = Math.Min(Math.Min(liquidityA, liquidityB), liquidityC);
        var optimalSize = Math.Min(maxByLiquidity, _maxPositionSize);

        // Apply risk-based sizing (reduce size if profit margin is small)
        if (opportunity.ProfitPercentage < 0.005m) // Less than 0.5% profit
        {
            optimalSize *= 0.5m; // Reduce size by half
        }

        return Math.Max(0, optimalSize);
    }

    /// <summary>
    /// Create order intent for specific arbitrage leg
    /// </summary>
    private OrderIntent? CreateArbitrageLegOrder(string symbol, ArbitrageDirection direction, decimal size, int leg)
    {
        if (!_orderBooks.TryGetValue(symbol, out var orderBook))
            return null;

        var symbolId = GetSymbolId(symbol);
        var bestPrice = GetBestPrice(orderBook);

        if (bestPrice == null)
            return null;

        // Determine order side based on direction and leg
        var side = DetermineOrderSide(direction, leg);
        var price = side == Side.Buy ? bestPrice.Value.ask : bestPrice.Value.bid;

        return new OrderIntent(
            clientOrderId: GenerateOrderId(),
            type: OrderType.Limit,
            side: side,
            priceTicks: DecimalToPriceTicks(price),
            quantity: DecimalToQuantityTicks(size),
            timestampIn: TimestampUtils.GetTimestampMicros(),
            symbolId: symbolId
        );
    }

    private Side DetermineOrderSide(ArbitrageDirection direction, int leg)
    {
        // Complex logic based on currency pair structure and arbitrage direction
        // This is simplified - real implementation would parse currency pairs
        return (direction, leg) switch
        {
            (ArbitrageDirection.Forward, 1) => Side.Buy,   // Buy first pair
            (ArbitrageDirection.Forward, 2) => Side.Sell,  // Sell for second pair
            (ArbitrageDirection.Forward, 3) => Side.Sell,  // Sell for base currency
            (ArbitrageDirection.Reverse, 1) => Side.Sell,  // Reverse direction
            (ArbitrageDirection.Reverse, 2) => Side.Buy,
            (ArbitrageDirection.Reverse, 3) => Side.Buy,
            _ => Side.Buy
        };
    }

    /// <summary>
    /// Get best bid/ask prices from order book
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (decimal bid, decimal ask)? GetBestPrice(OrderBook orderBook)
    {
        var bestBid = orderBook.GetBestBid();
        var bestAsk = orderBook.GetBestAsk();

        if (bestBid.priceTicks > 0 && bestAsk.priceTicks > 0)
        {
            return (PriceTicksToDecimal(bestBid.priceTicks), PriceTicksToDecimal(bestAsk.priceTicks));
        }

        return null;
    }

    private decimal GetAvailableLiquidity(string symbol, ArbitrageDirection direction)
    {
        if (!_orderBooks.TryGetValue(symbol, out var orderBook))
            return 0m;

        // Calculate available liquidity at top 3 levels
        var bidLevels = orderBook.GetTopLevels(Side.Buy, 3).ToArray();
        var askLevels = orderBook.GetTopLevels(Side.Sell, 3).ToArray();

        decimal totalLiquidity = 0m;
        for (int i = 0; i < Math.Min(3, Math.Max(bidLevels.Length, askLevels.Length)); i++)
        {
            var bidLevel = i < bidLevels.Length ? bidLevels[i] : null;
            var askLevel = i < askLevels.Length ? askLevels[i] : null;

            if (bidLevel != null && !bidLevel.IsEmpty)
                totalLiquidity += PriceTicksToDecimal(bidLevel.TotalQuantity);

            if (askLevel != null && !askLevel.IsEmpty)
                totalLiquidity += PriceTicksToDecimal(askLevel.TotalQuantity);
        }

        return totalLiquidity * 0.8m; // Use 80% of available liquidity for safety
    }

    public Task StartAsync()
    {
        State = AdvancedStrategyState.Running;
        _logger.LogInformation("Triangular arbitrage strategy started");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        State = AdvancedStrategyState.Stopped;
        _logger.LogInformation("Triangular arbitrage strategy stopped");
        return Task.CompletedTask;
    }

    public StrategyStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new StrategyStatistics
            {
                StrategyName = Name,
                TotalSignals = _opportunitiesDetected,
                ExecutedSignals = _opportunitiesExecuted,
                SuccessRate = _opportunitiesDetected > 0 ? (decimal)_opportunitiesExecuted / _opportunitiesDetected : 0m,
                TotalPnL = _totalProfitRealized,
                Sharpe = CalculateSharpeRatio(),
                MaxDrawdown = 0m, // TODO: Implement drawdown tracking
                ActivePositions = 0 // Arbitrage is market neutral
            };
        }
    }

    private decimal CalculateSharpeRatio()
    {
        // Simplified Sharpe calculation
        if (_opportunitiesExecuted == 0) return 0m;

        var averageReturn = _totalProfitRealized / _opportunitiesExecuted;
        var riskFreeRate = 0.02m / 365m; // 2% annual risk-free rate, daily

        // Assume low volatility for arbitrage strategies
        var volatility = averageReturn * 0.1m; // 10% of average return as volatility estimate

        return volatility > 0 ? (averageReturn - riskFreeRate) / volatility : 0m;
    }

    // Helper methods (simplified implementations)
    private string GetSymbolName(int symbolId) => symbolId switch
    {
        1 => "BTCUSDT",
        2 => "ETHUSDT",
        3 => "ADAUSDT",
        _ => "UNKNOWN"
    };

    private int GetSymbolId(string symbol) => symbol switch
    {
        "BTCUSDT" => 1,
        "ETHUSDT" => 2,
        "ADAUSDT" => 3,
        _ => 0
    };

    private long GenerateOrderId() => TimestampUtils.GetTimestampMicros();
    private decimal PriceTicksToDecimal(long ticks) => ticks * 0.01m;
    private long DecimalToPriceTicks(decimal price) => (long)(price * 100);
    private long DecimalToQuantityTicks(decimal quantity) => (long)(quantity * 100_000_000);
}

/// <summary>
/// Represents a detected triangular arbitrage opportunity
/// </summary>
public record ArbitrageOpportunity
{
    public required string PairA { get; init; }
    public required string PairB { get; init; }
    public required string PairC { get; init; }
    public required ArbitrageDirection Direction { get; init; }
    public required decimal ProfitAmount { get; init; }
    public required decimal ProfitPercentage { get; init; }
    public required decimal StartAmount { get; init; }
    public required decimal ExpectedEndAmount { get; init; }
    public required long DetectedAt { get; init; }
    public required decimal PriceA { get; init; }
    public required decimal PriceB { get; init; }
    public required decimal PriceC { get; init; }
}

public enum ArbitrageDirection
{
    Forward,  // A → B → C → A
    Reverse   // A ← B ← C ← A
}


