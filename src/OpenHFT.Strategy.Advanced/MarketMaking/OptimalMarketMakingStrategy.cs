using OpenHFT.Core.Models;
using OpenHFT.Book.Models;
using OpenHFT.Book.Core;
using OpenHFT.Strategy.Interfaces;
using Microsoft.Extensions.Logging;
using MathNet.Numerics.Statistics;
using System.Runtime.CompilerServices;

namespace OpenHFT.Strategy.Advanced.MarketMaking;

/// <summary>
/// Advanced market making strategy with inventory optimization, adaptive spreads,
/// and sophisticated risk management. Maintains market neutrality while maximizing spread capture.
/// </summary>
public class OptimalMarketMakingStrategy : IAdvancedStrategy
{
    private readonly ILogger<OptimalMarketMakingStrategy> _logger;
    private readonly Dictionary<int, SymbolContext> _symbolContexts;
    private readonly object _contextLock = new();

    // Strategy parameters
    private readonly decimal _baseSpreadBps = 5m;        // 5 basis points base spread
    private readonly decimal _maxInventoryRatio = 0.2m;  // 20% max inventory vs daily volume
    private readonly decimal _skewMultiplier = 2.0m;     // Inventory skew adjustment factor
    private readonly int _volumeWindow = 100;            // Rolling window for volume analysis
    private readonly decimal _minQuoteSize = 0.1m;       // Minimum quote size
    private readonly decimal _maxQuoteSize = 10m;        // Maximum quote size

    // Performance tracking
    private long _quotesPosted;
    private long _executionCount;
    private decimal _totalSpreadCaptured;
    private decimal _inventoryPnL;
    private readonly object _statsLock = new();

    public OptimalMarketMakingStrategy(ILogger<OptimalMarketMakingStrategy> logger)
    {
        _logger = logger;
        _symbolContexts = new Dictionary<int, SymbolContext>();
    }

    public string Name => "OptimalMarketMaking";
    public AdvancedStrategyState State { get; private set; } = AdvancedStrategyState.Stopped;

    public async Task<List<OrderIntent>> ProcessMarketData(MarketDataEvent marketData, OrderBook orderBook)
    {
        var symbolId = marketData.InstrumentId;
        var context = GetOrCreateSymbolContext(symbolId);

        // Update context with new market data
        UpdateSymbolContext(context, marketData, orderBook);

        // Only quote on trade events for better signal quality
        if (marketData.Kind != EventKind.Trade)
            return new List<OrderIntent>();

        // Increment execution counter
        Interlocked.Increment(ref _executionCount);

        // Log strategy execution every 200 calls
        if (_executionCount % 200 == 0)
        {
            _logger.LogInformation("OptimalMarketMaking: Processing SymbolId {SymbolId} (Execution #{Count})",
                context.SymbolId, _executionCount);
        }

        // Calculate optimal quotes
        var quotes = await CalculateOptimalQuotes(context, orderBook);

        // Update statistics
        if (quotes.Any())
        {
            Interlocked.Add(ref _quotesPosted, quotes.Count());
        }

        return quotes.ToList();
    }

    /// <summary>
    /// Calculate optimal bid/ask quotes with inventory optimization
    /// </summary>
    private async Task<List<OrderIntent>> CalculateOptimalQuotes(SymbolContext context, OrderBook orderBook)
    {
        var quotes = new List<OrderIntent>();

        try
        {
            // Get current market state
            var midPrice = CalculateMidPrice(orderBook);
            if (!midPrice.HasValue)
                return quotes;

            // Calculate dynamic spread based on market conditions
            var adaptiveSpread = CalculateAdaptiveSpread(context, orderBook);

            // Calculate inventory skew adjustment
            var inventorySkew = CalculateInventorySkew(context);

            // Calculate optimal quote prices with skew
            var bidPrice = midPrice.Value - (adaptiveSpread / 2m) + inventorySkew;
            var askPrice = midPrice.Value + (adaptiveSpread / 2m) + inventorySkew;

            // Calculate optimal quote sizes
            var optimalSizes = CalculateOptimalQuoteSizes(context, orderBook, midPrice.Value);

            // Create bid quote
            if (optimalSizes.bidSize >= _minQuoteSize && ShouldQuoteBid(context, bidPrice))
            {
                var bidOrder = new OrderIntent(
                    clientOrderId: GenerateOrderId(),
                    type: OrderType.Limit,
                    side: Side.Buy,
                    priceTicks: DecimalToPriceTicks(bidPrice),
                    quantity: DecimalToQuantityTicks(optimalSizes.bidSize),
                    timestampIn: TimestampUtils.GetTimestampMicros(),
                    symbolId: context.SymbolId
                );
                quotes.Add(bidOrder);
            }

            // Create ask quote
            if (optimalSizes.askSize >= _minQuoteSize && ShouldQuoteAsk(context, askPrice))
            {
                var askOrder = new OrderIntent(
                    clientOrderId: GenerateOrderId(),
                    type: OrderType.Limit,
                    side: Side.Sell,
                    priceTicks: DecimalToPriceTicks(askPrice),
                    quantity: DecimalToQuantityTicks(optimalSizes.askSize),
                    timestampIn: TimestampUtils.GetTimestampMicros(),
                    symbolId: context.SymbolId
                );
                quotes.Add(askOrder);
            }

            // Log quote generation
            if (quotes.Count > 0)
            {
                _logger.LogDebug(
                    "Generated quotes for symbol {SymbolId}: Bid={Bid:F4}@{BidSize:F2}, Ask={Ask:F4}@{AskSize:F2}, " +
                    "Spread={Spread:F4}, Skew={Skew:F4}, Inventory={Inventory:F2}",
                    context.SymbolId, bidPrice, optimalSizes.bidSize, askPrice, optimalSizes.askSize,
                    adaptiveSpread, inventorySkew, context.NetInventory);
            }

            // Update context with quote information
            context.LastBidPrice = bidPrice;
            context.LastAskPrice = askPrice;
            context.LastQuoteTime = TimestampUtils.GetTimestampMicros();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating optimal quotes for symbol {SymbolId}", context.SymbolId);
        }

        return quotes;
    }

    /// <summary>
    /// Calculate adaptive spread based on market volatility and order book depth
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private decimal CalculateAdaptiveSpread(SymbolContext context, OrderBook orderBook)
    {
        var baseSpread = _baseSpreadBps / 10000m; // Convert basis points to decimal

        // Volatility adjustment
        var volatilityMultiplier = Math.Max(1.0m, Math.Min(5.0m, context.VolatilityEstimate * 10m));

        // Order book depth adjustment
        var depthMultiplier = CalculateDepthMultiplier(orderBook);

        // Volume-based adjustment
        var volumeMultiplier = CalculateVolumeMultiplier(context);

        // Time-of-day adjustment (wider spreads during low liquidity periods)
        var timeMultiplier = CalculateTimeMultiplier();

        var adaptiveSpread = baseSpread * volatilityMultiplier * depthMultiplier * volumeMultiplier * timeMultiplier;

        // Ensure minimum and maximum spread limits
        return Math.Max(0.0001m, Math.Min(0.01m, adaptiveSpread)); // 1bp to 100bp range
    }

    /// <summary>
    /// Calculate inventory skew to encourage inventory neutrality
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private decimal CalculateInventorySkew(SymbolContext context)
    {
        if (context.RollingVolume.Count == 0)
            return 0m;

        var avgVolume = context.RollingVolume.Average();
        if (avgVolume <= 0)
            return 0m;

        // Calculate inventory ratio as percentage of recent volume
        var inventoryRatio = context.NetInventory / (avgVolume * _maxInventoryRatio);

        // Apply sigmoid function for smooth skew adjustment
        var skewAdjustment = _skewMultiplier * (decimal)Math.Tanh((double)inventoryRatio);

        // Convert to price adjustment (positive skew = higher prices to encourage selling)
        return skewAdjustment * context.LastMidPrice * 0.001m; // Max 0.1% price skew
    }

    /// <summary>
    /// Calculate optimal quote sizes based on market conditions and inventory
    /// </summary>
    private (decimal bidSize, decimal askSize) CalculateOptimalQuoteSizes(
        SymbolContext context, OrderBook orderBook, decimal midPrice)
    {
        // Base size calculation using Kelly criterion approximation
        var baseSize = CalculateKellyOptimalSize(context, midPrice);

        // Adjust sizes based on inventory position
        var inventoryFactor = CalculateInventoryFactor(context);

        // Adjust based on order book imbalance
        var imbalanceFactor = CalculateImbalanceFactor(orderBook);

        var bidSize = baseSize * (1m + inventoryFactor) * imbalanceFactor.bidFactor;
        var askSize = baseSize * (1m - inventoryFactor) * imbalanceFactor.askFactor;

        // Apply size limits
        bidSize = Math.Max(_minQuoteSize, Math.Min(_maxQuoteSize, bidSize));
        askSize = Math.Max(_minQuoteSize, Math.Min(_maxQuoteSize, askSize));

        return (bidSize, askSize);
    }

    /// <summary>
    /// Calculate Kelly optimal position size
    /// </summary>
    private decimal CalculateKellyOptimalSize(SymbolContext context, decimal midPrice)
    {
        if (context.WinRate <= 0 || context.VolatilityEstimate <= 0)
            return _minQuoteSize;

        // Kelly formula: f = (bp - q) / b
        // where b = odds received, p = probability of winning, q = probability of losing
        var p = context.WinRate;
        var q = 1m - p;
        var b = context.AverageSpreadCapture / context.VolatilityEstimate; // Risk-adjusted return

        var kellyFraction = (b * p - q) / b;

        // Apply conservative scaling (use 25% of Kelly)
        kellyFraction = Math.Max(0.01m, Math.Min(0.25m, kellyFraction * 0.25m));

        // Convert to position size based on available capital
        var availableCapital = 10000m; // Assume $10k available capital per symbol
        var positionValue = availableCapital * kellyFraction;

        return positionValue / midPrice;
    }

    /// <summary>
    /// Calculate inventory adjustment factor for position sizing
    /// </summary>
    private decimal CalculateInventoryFactor(SymbolContext context)
    {
        if (context.RollingVolume.Count == 0)
            return 0m;

        var avgVolume = context.RollingVolume.Average();
        var inventoryRatio = avgVolume > 0 ? context.NetInventory / (avgVolume * _maxInventoryRatio) : 0m;

        // Return factor between -0.5 and 0.5
        return Math.Max(-0.5m, Math.Min(0.5m, inventoryRatio));
    }

    /// <summary>
    /// Calculate order book imbalance factors
    /// </summary>
    private (decimal bidFactor, decimal askFactor) CalculateImbalanceFactor(OrderBook orderBook)
    {
        var bidDepth = CalculateOrderBookDepth(orderBook, Side.Buy, 5);
        var askDepth = CalculateOrderBookDepth(orderBook, Side.Sell, 5);

        var totalDepth = bidDepth + askDepth;
        if (totalDepth <= 0)
            return (1m, 1m);

        var bidRatio = bidDepth / totalDepth;
        var askRatio = askDepth / totalDepth;

        // If more depth on bid side, increase ask size and decrease bid size
        var bidFactor = 0.5m + askRatio; // Range: 0.5 to 1.0
        var askFactor = 0.5m + bidRatio; // Range: 0.5 to 1.0

        return (bidFactor, askFactor);
    }

    /// <summary>
    /// Calculate order book depth for given side and levels
    /// </summary>
    private decimal CalculateOrderBookDepth(OrderBook orderBook, Side side, int levels)
    {
        decimal totalDepth = 0m;

        var bookLevels = orderBook.GetTopLevels(side, levels).ToArray();

        for (int i = 0; i < bookLevels.Length; i++)
        {
            var level = bookLevels[i];
            if (level != null && !level.IsEmpty)
            {
                totalDepth += PriceTicksToDecimal(level.TotalQuantity);
            }
        }

        return totalDepth;
    }

    private decimal CalculateDepthMultiplier(OrderBook orderBook)
    {
        var bidDepth = CalculateOrderBookDepth(orderBook, Side.Buy, 3);
        var askDepth = CalculateOrderBookDepth(orderBook, Side.Sell, 3);
        var totalDepth = bidDepth + askDepth;

        // Lower depth = wider spreads (higher multiplier)
        if (totalDepth < 1m) return 3.0m;
        if (totalDepth < 5m) return 2.0m;
        if (totalDepth < 10m) return 1.5m;
        return 1.0m;
    }

    private decimal CalculateVolumeMultiplier(SymbolContext context)
    {
        if (context.RollingVolume.Count < 10)
            return 1.5m; // Higher spread for insufficient volume data

        var recentVolume = context.RollingVolume.TakeLast(10).Average();
        var historicalAvg = context.RollingVolume.Average();

        if (historicalAvg <= 0)
            return 1.5m;

        var volumeRatio = recentVolume / historicalAvg;

        // Lower volume = wider spreads
        return volumeRatio switch
        {
            < 0.3m => 2.5m,
            < 0.5m => 2.0m,
            < 0.8m => 1.5m,
            > 2.0m => 0.8m,
            > 1.5m => 0.9m,
            _ => 1.0m
        };
    }

    private decimal CalculateTimeMultiplier()
    {
        var hour = DateTime.UtcNow.Hour;

        // Wider spreads during low liquidity hours
        return hour switch
        {
            >= 22 or <= 6 => 1.5m,  // Asian overnight hours
            >= 7 and <= 9 => 1.2m,  // Pre-market
            >= 16 and <= 18 => 1.2m, // After-hours
            _ => 1.0m                // Regular trading hours
        };
    }

    private bool ShouldQuoteBid(SymbolContext context, decimal bidPrice)
    {
        // Don't quote if inventory is too long
        var avgVolume = context.RollingVolume.Count > 0 ? context.RollingVolume.Average() : 0m;
        var maxInventory = avgVolume * _maxInventoryRatio;

        return context.NetInventory < maxInventory;
    }

    private bool ShouldQuoteAsk(SymbolContext context, decimal askPrice)
    {
        // Don't quote if inventory is too short
        var avgVolume = context.RollingVolume.Count > 0 ? context.RollingVolume.Average() : 0m;
        var maxInventory = avgVolume * _maxInventoryRatio;

        return context.NetInventory > -maxInventory;
    }

    /// <summary>
    /// Update symbol context with new market data
    /// </summary>
    private void UpdateSymbolContext(SymbolContext context, MarketDataEvent marketEvent, OrderBook orderBook)
    {
        lock (context.UpdateLock)
        {
            // Update mid price
            var midPrice = CalculateMidPrice(orderBook);
            if (midPrice.HasValue)
            {
                context.LastMidPrice = midPrice.Value;
                context.PriceHistory.Add(midPrice.Value);

                // Maintain rolling window
                if (context.PriceHistory.Count > _volumeWindow)
                {
                    context.PriceHistory.RemoveAt(0);
                }

                // Update volatility estimate
                UpdateVolatilityEstimate(context);
            }

            // Update volume on trade events
            if (marketEvent.Kind == EventKind.Trade)
            {
                var volume = PriceTicksToDecimal(marketEvent.Quantity);
                context.RollingVolume.Add(volume);

                if (context.RollingVolume.Count > _volumeWindow)
                {
                    context.RollingVolume.RemoveAt(0);
                }
            }

            context.LastUpdateTime = TimestampUtils.GetTimestampMicros();
        }
    }

    private void UpdateVolatilityEstimate(SymbolContext context)
    {
        if (context.PriceHistory.Count < 10)
            return;

        // Calculate returns
        var returns = new List<decimal>();
        for (int i = 1; i < context.PriceHistory.Count; i++)
        {
            if (context.PriceHistory[i - 1] > 0)
            {
                var return_ = (context.PriceHistory[i] / context.PriceHistory[i - 1]) - 1m;
                returns.Add(return_);
            }
        }

        if (returns.Count > 0)
        {
            // Use exponentially weighted moving average for volatility
            var variance = (decimal)returns.Select(r => (double)r).Variance();
            context.VolatilityEstimate = (decimal)Math.Sqrt((double)variance);

            // Apply EWMA smoothing
            context.VolatilityEstimate = context.VolatilityEstimate * 0.1m + context.VolatilityEstimate * 0.9m;
        }
    }

    private SymbolContext GetOrCreateSymbolContext(int symbolId)
    {
        lock (_contextLock)
        {
            if (!_symbolContexts.TryGetValue(symbolId, out var context))
            {
                context = new SymbolContext(symbolId);
                _symbolContexts[symbolId] = context;
            }
            return context;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private decimal? CalculateMidPrice(OrderBook orderBook)
    {
        var bestBid = orderBook.GetBestBid();
        var bestAsk = orderBook.GetBestAsk();

        if (bestBid.priceTicks > 0 && bestAsk.priceTicks > 0)
        {
            var bidPrice = PriceTicksToDecimal(bestBid.priceTicks);
            var askPrice = PriceTicksToDecimal(bestAsk.priceTicks);
            return (bidPrice + askPrice) / 2m;
        }

        return null;
    }

    public Task StartAsync()
    {
        State = AdvancedStrategyState.Running;
        _logger.LogInformation("Optimal market making strategy started");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        State = AdvancedStrategyState.Stopped;
        _logger.LogInformation("Optimal market making strategy stopped");
        return Task.CompletedTask;
    }

    public StrategyStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            var totalSymbols = _symbolContexts.Count;
            var avgInventory = totalSymbols > 0 ? _symbolContexts.Values.Average(c => Math.Abs(c.NetInventory)) : 0m;

            return new StrategyStatistics
            {
                StrategyName = Name,
                TotalSignals = _quotesPosted,
                ExecutedSignals = _quotesPosted, // All quotes are "executed"
                SuccessRate = 1.0m, // Market making success measured differently
                TotalPnL = _totalSpreadCaptured + _inventoryPnL,
                Sharpe = CalculateSharpeRatio(),
                MaxDrawdown = 0m, // TODO: Implement drawdown tracking
                ActivePositions = (long)avgInventory
            };
        }
    }

    private decimal CalculateSharpeRatio()
    {
        // Simplified Sharpe for market making
        if (_quotesPosted == 0) return 0m;

        var totalPnL = _totalSpreadCaptured + _inventoryPnL;
        var avgReturn = totalPnL / _quotesPosted;
        var riskFreeRate = 0.02m / 365m;

        // Market making typically has low volatility
        var volatility = Math.Max(0.001m, avgReturn * 0.2m);

        return (avgReturn - riskFreeRate) / volatility;
    }

    // Helper methods
    private long GenerateOrderId() => TimestampUtils.GetTimestampMicros();
    private decimal PriceTicksToDecimal(long ticks) => ticks * 0.01m;
    private long DecimalToPriceTicks(decimal price) => (long)(price * 100);
    private long DecimalToQuantityTicks(decimal quantity) => (long)(quantity * 100_000_000);
}

/// <summary>
/// Context information for each trading symbol
/// </summary>
public class SymbolContext
{
    public int SymbolId { get; }
    public decimal LastMidPrice { get; set; }
    public decimal NetInventory { get; set; }
    public decimal VolatilityEstimate { get; set; } = 0.01m;
    public decimal WinRate { get; set; } = 0.6m;
    public decimal AverageSpreadCapture { get; set; } = 0.0005m;

    public List<decimal> PriceHistory { get; } = new();
    public List<decimal> RollingVolume { get; } = new();

    public decimal LastBidPrice { get; set; }
    public decimal LastAskPrice { get; set; }
    public long LastQuoteTime { get; set; }
    public long LastUpdateTime { get; set; }

    public readonly object UpdateLock = new();

    public SymbolContext(int symbolId)
    {
        SymbolId = symbolId;
    }
}
