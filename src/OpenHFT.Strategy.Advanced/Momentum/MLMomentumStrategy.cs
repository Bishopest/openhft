using OpenHFT.Core.Models;
using OpenHFT.Book.Models;
using OpenHFT.Book.Core;
using OpenHFT.Strategy.Interfaces;
using Microsoft.Extensions.Logging;
using MathNet.Numerics.Statistics;
using System.Runtime.CompilerServices;

namespace OpenHFT.Strategy.Advanced.Momentum;

/// <summary>
/// Advanced momentum strategy using multiple timeframes, machine learning predictions,
/// and sophisticated signal filtering. Captures short-term momentum while avoiding false breakouts.
/// </summary>
public class MLMomentumStrategy : IAdvancedStrategy
{
    private readonly ILogger<MLMomentumStrategy> _logger;
    private readonly Dictionary<int, MomentumContext> _symbolContexts;
    private readonly object _contextLock = new();

    // Strategy parameters
    private readonly int _fastPeriod = 10;           // Fast EMA period
    private readonly int _slowPeriod = 30;           // Slow EMA period
    private readonly int _volumePeriod = 20;         // Volume SMA period
    private const decimal _momentumThreshold = 0.002m; // 0.2% momentum threshold
    private readonly decimal _volumeMultiplier = 1.5m;    // Volume confirmation multiplier
    private readonly decimal _maxPositionSize = 5m;       // Maximum position size
    private readonly TimeSpan _holdingPeriod = TimeSpan.FromMinutes(5); // Max holding time

    // ML-like feature weights (simplified online learning simulation)
    private decimal _priceFeatureWeight = 0.4m;
    private decimal _volumeFeatureWeight = 0.3m;
    private decimal _volatilityFeatureWeight = 0.2m;
    private decimal _orderBookFeatureWeight = 0.1m;

    // Performance tracking
    private long _signalsGenerated;
    private long _executionCount;
    private long _signalsExecuted;
    private decimal _totalPnL;
    private long _correctPredictions;
    private readonly object _statsLock = new();

    public MLMomentumStrategy(ILogger<MLMomentumStrategy> logger)
    {
        _logger = logger;
        _symbolContexts = new Dictionary<int, MomentumContext>();
    }

    public string Name => "MLMomentum";
    public AdvancedStrategyState State { get; private set; } = AdvancedStrategyState.Stopped;

    public async Task<List<OrderIntent>> ProcessMarketData(MarketDataEvent marketData, OrderBook orderBook)
    {
        var symbolId = marketData.InstrumentId;
        var context = GetOrCreateMomentumContext(symbolId);

        // Update context with new market data
        UpdateMomentumContext(context, marketData, orderBook);

        // Only generate signals on trade events
        if (marketData.Kind != EventKind.Trade)
            return await CheckExistingPositions(context);

        // Increment execution counter
        Interlocked.Increment(ref _executionCount);

        // Log strategy execution every 300 calls
        if (_executionCount % 300 == 0)
        {
            _logger.LogInformation("MLMomentum: Processing SymbolId {SymbolId} (Execution #{Count})",
                symbolId, _executionCount);
        }

        // Generate momentum signals
        var signals = await GenerateMomentumSignals(context, orderBook);

        // Update statistics
        if (signals.Any())
        {
            Interlocked.Increment(ref _signalsGenerated);
        }

        return signals;
    }

    /// <summary>
    /// Generate momentum signals using ML-inspired feature analysis
    /// </summary>
    private async Task<List<OrderIntent>> GenerateMomentumSignals(MomentumContext context, OrderBook orderBook)
    {
        var signals = new List<OrderIntent>();

        try
        {
            // Ensure we have enough data
            if (context.PriceHistory.Count < _slowPeriod)
                return signals;

            // Calculate technical indicators
            var technicalSignals = CalculateTechnicalSignals(context);

            // Calculate ML features
            var mlFeatures = CalculateMLFeatures(context, orderBook);

            // Combine signals using weighted scoring
            var combinedScore = CalculateCombinedMomentumScore(technicalSignals, mlFeatures);

            // Generate trading signal if score exceeds threshold
            var signal = EvaluateSignalStrength(combinedScore, context);

            if (signal.direction != SignalDirection.None)
            {
                var order = await CreateMomentumOrder(signal, context, orderBook);
                if (order != null)
                {
                    signals.Add(order.Value);
                    Interlocked.Increment(ref _signalsExecuted);

                    // Update position tracking
                    UpdatePositionTracking(context, signal, order);

                    _logger.LogInformation(
                        "Generated momentum signal for symbol {SymbolId}: {Direction} | " +
                        "Score: {Score:F4} | Size: {Size:F2} | Price: {Price:F4}",
                        context.SymbolId, signal.direction, combinedScore.totalScore,
                        PriceTicksToDecimal(order.Value.Quantity), PriceTicksToDecimal(order.Value.PriceTicks));
                }
            }

            // Update model weights based on recent performance (simplified online learning)
            UpdateModelWeights(context, combinedScore);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating momentum signals for symbol {SymbolId}", context.SymbolId);
        }

        return signals;
    }

    /// <summary>
    /// Calculate traditional technical analysis signals
    /// </summary>
    private TechnicalSignals CalculateTechnicalSignals(MomentumContext context)
    {
        var signals = new TechnicalSignals();

        // Calculate EMAs
        var fastEMA = CalculateEMA(context.PriceHistory, _fastPeriod);
        var slowEMA = CalculateEMA(context.PriceHistory, _slowPeriod);

        context.FastEMA = fastEMA;
        context.SlowEMA = slowEMA;

        // EMA crossover signal
        if (fastEMA > slowEMA)
        {
            signals.EmaCrossSignal = (fastEMA - slowEMA) / slowEMA;
        }
        else
        {
            signals.EmaCrossSignal = (fastEMA - slowEMA) / slowEMA;
        }

        // Price momentum
        if (context.PriceHistory.Count >= 10)
        {
            var currentPrice = context.PriceHistory.Last();
            var priceNPeriodsAgo = context.PriceHistory[^10];
            signals.PriceMomentum = (currentPrice - priceNPeriodsAgo) / priceNPeriodsAgo;
        }

        // Volume momentum
        if (context.VolumeHistory.Count >= _volumePeriod)
        {
            var avgVolume = context.VolumeHistory.TakeLast(_volumePeriod).Average();
            var recentVolume = context.VolumeHistory.TakeLast(5).Average();
            signals.VolumeMomentum = avgVolume > 0 ? (recentVolume - avgVolume) / avgVolume : 0m;
        }

        // Volatility signal
        signals.VolatilitySignal = CalculateVolatilitySignal(context);

        return signals;
    }

    /// <summary>
    /// Calculate ML-inspired features for momentum prediction
    /// </summary>
    private MLFeatures CalculateMLFeatures(MomentumContext context, OrderBook orderBook)
    {
        var features = new MLFeatures();

        // Order book imbalance feature
        features.OrderBookImbalance = CalculateOrderBookImbalance(orderBook);

        // Price acceleration feature
        features.PriceAcceleration = CalculatePriceAcceleration(context);

        // Volume-price divergence
        features.VolumePriceDivergence = CalculateVolumePriceDivergence(context);

        // Microstructure features
        features.TickDirection = context.LastTickDirection;
        features.TickSize = context.LastTickSize;

        // Time-based features
        features.TimeDecay = CalculateTimeDecay(context);

        // Volatility regime
        features.VolatilityRegime = ClassifyVolatilityRegime(context);

        return features;
    }

    /// <summary>
    /// Combine technical and ML signals using weighted scoring
    /// </summary>
    private (decimal totalScore, SignalDirection direction) CalculateCombinedMomentumScore(
        TechnicalSignals technical, MLFeatures mlFeatures)
    {
        // Technical component
        var technicalScore =
            technical.EmaCrossSignal * 0.3m +
            technical.PriceMomentum * 0.3m +
            technical.VolumeMomentum * 0.2m +
            technical.VolatilitySignal * 0.2m;

        // ML component
        var mlScore =
            mlFeatures.OrderBookImbalance * _orderBookFeatureWeight +
            mlFeatures.PriceAcceleration * _priceFeatureWeight +
            mlFeatures.VolumePriceDivergence * _volumeFeatureWeight +
            mlFeatures.VolatilityRegime * _volatilityFeatureWeight;

        // Combine with adaptive weights
        var totalScore = technicalScore * 0.6m + mlScore * 0.4m;

        // Apply time decay
        totalScore *= (1m - mlFeatures.TimeDecay * 0.1m);

        // Determine direction
        var direction = totalScore switch
        {
            > _momentumThreshold => SignalDirection.Long,
            < -_momentumThreshold => SignalDirection.Short,
            _ => SignalDirection.None
        };

        return (totalScore, direction);
    }

    /// <summary>
    /// Evaluate signal strength and apply additional filters
    /// </summary>
    private (SignalDirection direction, decimal confidence, decimal size) EvaluateSignalStrength(
        (decimal totalScore, SignalDirection direction) signal, MomentumContext context)
    {
        if (signal.direction == SignalDirection.None)
            return (SignalDirection.None, 0m, 0m);

        // Calculate confidence based on signal strength
        var confidence = Math.Min(1m, Math.Abs(signal.totalScore) / (_momentumThreshold * 3m));

        // Apply additional filters

        // 1. Don't trade against strong trend
        if (context.FastEMA > 0 && context.SlowEMA > 0)
        {
            var trendDirection = context.FastEMA > context.SlowEMA ? 1 : -1;
            var signalDirection = signal.direction == SignalDirection.Long ? 1 : -1;

            if (trendDirection != signalDirection && confidence < 0.8m)
            {
                return (SignalDirection.None, 0m, 0m);
            }
        }

        // 2. Require volume confirmation for strong signals
        if (context.VolumeHistory.Count >= _volumePeriod)
        {
            var avgVolume = context.VolumeHistory.TakeLast(_volumePeriod).Average();
            var currentVolume = context.VolumeHistory.LastOrDefault();

            if (currentVolume < avgVolume * _volumeMultiplier && confidence > 0.5m)
            {
                confidence *= 0.7m; // Reduce confidence without volume confirmation
            }
        }

        // 3. Check position limits
        var existingPosition = context.CurrentPosition;
        var maxAdditionalSize = _maxPositionSize - Math.Abs(existingPosition);

        if (maxAdditionalSize <= 0.1m)
        {
            return (SignalDirection.None, 0m, 0m);
        }

        // Calculate position size based on confidence and Kelly criterion
        var baseSize = CalculateKellySize(context, confidence);
        var adjustedSize = Math.Min(baseSize, maxAdditionalSize);

        // Minimum viable size check
        if (adjustedSize < 0.1m)
        {
            return (SignalDirection.None, 0m, 0m);
        }

        return (signal.direction, confidence, adjustedSize);
    }

    /// <summary>
    /// Create momentum-based order
    /// </summary>
    private async Task<OrderIntent?> CreateMomentumOrder(
        (SignalDirection direction, decimal confidence, decimal size) signal,
        MomentumContext context, OrderBook orderBook)
    {
        var midPrice = CalculateMidPrice(orderBook);
        if (!midPrice.HasValue)
            return null;

        // Calculate entry price with smart limit pricing
        var limitPrice = CalculateSmartLimitPrice(signal.direction, midPrice.Value, orderBook, signal.confidence);

        var side = signal.direction == SignalDirection.Long ? Side.Buy : Side.Sell;

        return new OrderIntent(
            clientOrderId: GenerateOrderId(),
            type: OrderTypeEnum.Limit,
            side: side,
            priceTicks: DecimalToPriceTicks(limitPrice),
            quantity: DecimalToQuantityTicks(signal.size),
            timestampIn: TimestampUtils.GetTimestampMicros(),
            symbolId: context.SymbolId
        );
    }

    /// <summary>
    /// Calculate smart limit price based on order book and signal confidence
    /// </summary>
    private decimal CalculateSmartLimitPrice(SignalDirection direction, decimal midPrice,
        OrderBook orderBook, decimal confidence)
    {
        var spread = CalculateSpread(orderBook);

        // High confidence = aggressive pricing (closer to market)
        // Low confidence = passive pricing (better price, but lower fill probability)
        var aggressiveness = confidence * 0.8m; // Max 80% through spread

        if (direction == SignalDirection.Long)
        {
            // For buying, start from bid and move toward ask
            var bestBid = orderBook.GetBestBid();
            var bidPrice = bestBid.priceTicks > 0 ? PriceTicksToDecimal(bestBid.priceTicks) : midPrice;
            return bidPrice + (spread * aggressiveness);
        }
        else
        {
            // For selling, start from ask and move toward bid
            var bestAsk = orderBook.GetBestAsk();
            var askPrice = bestAsk.priceTicks > 0 ? PriceTicksToDecimal(bestAsk.priceTicks) : midPrice;
            return askPrice - (spread * aggressiveness);
        }
    }

    /// <summary>
    /// Check and manage existing positions
    /// </summary>
    private async Task<List<OrderIntent>> CheckExistingPositions(MomentumContext context)
    {
        var orders = new List<OrderIntent>();

        // Check if we should close positions based on time or profit targets
        if (Math.Abs(context.CurrentPosition) > 0.1m)
        {
            var holdingTime = TimeSpan.FromMicroseconds(TimestampUtils.GetTimestampMicros() - context.LastEntryTime);

            // Close position if holding too long or hit profit target
            if (holdingTime > _holdingPeriod || ShouldClosePosition(context))
            {
                var closeOrder = CreateClosePositionOrder(context);
                if (closeOrder != null)
                {
                    orders.Add(closeOrder.Value);

                    // Update PnL tracking
                    UpdatePnLTracking(context, closeOrder);
                }
            }
        }

        return orders;
    }

    private bool ShouldClosePosition(MomentumContext context)
    {
        // Close if unrealized PnL hits target (simplified)
        var unrealizedPnL = CalculateUnrealizedPnL(context);
        var targetProfit = Math.Abs(context.CurrentPosition) * 0.01m; // 1% profit target

        return unrealizedPnL > targetProfit || unrealizedPnL < -targetProfit * 2; // 2:1 risk ratio
    }

    private decimal CalculateUnrealizedPnL(MomentumContext context)
    {
        if (Math.Abs(context.CurrentPosition) < 0.001m)
            return 0m;

        var currentPrice = context.PriceHistory.LastOrDefault();
        var entryPrice = context.LastEntryPrice;

        if (currentPrice <= 0 || entryPrice <= 0)
            return 0m;

        return context.CurrentPosition * (currentPrice - entryPrice);
    }

    private OrderIntent? CreateClosePositionOrder(MomentumContext context)
    {
        if (Math.Abs(context.CurrentPosition) < 0.001m)
            return null;

        var side = context.CurrentPosition > 0 ? Side.Sell : Side.Buy;
        var size = Math.Abs(context.CurrentPosition);

        // Use market order for quick exit
        return new OrderIntent(
            clientOrderId: GenerateOrderId(),
            type: OrderTypeEnum.Market,
            side: side,
            priceTicks: 0, // Market order
            quantity: DecimalToQuantityTicks(size),
            timestampIn: TimestampUtils.GetTimestampMicros(),
            symbolId: context.SymbolId
        );
    }

    // Technical indicator calculations
    private decimal CalculateEMA(List<decimal> prices, int period)
    {
        if (prices.Count < period)
            return 0m;

        var multiplier = 2m / (period + 1);
        var ema = prices.Take(period).Average(); // Start with SMA

        for (int i = period; i < prices.Count; i++)
        {
            ema = (prices[i] * multiplier) + (ema * (1 - multiplier));
        }

        return ema;
    }

    private decimal CalculateVolatilitySignal(MomentumContext context)
    {
        if (context.PriceHistory.Count < 20)
            return 0m;

        // Calculate recent volatility vs historical
        var recentPrices = context.PriceHistory.TakeLast(10).ToList();
        var historicalPrices = context.PriceHistory.TakeLast(20).ToList();

        var recentVol = CalculateVolatility(recentPrices);
        var historicalVol = CalculateVolatility(historicalPrices);

        if (historicalVol <= 0)
            return 0m;

        // Positive signal when volatility is expanding (momentum environment)
        return (recentVol - historicalVol) / historicalVol;
    }

    private decimal CalculateVolatility(List<decimal> prices)
    {
        if (prices.Count < 2)
            return 0m;

        var returns = new List<decimal>();
        for (int i = 1; i < prices.Count; i++)
        {
            if (prices[i - 1] > 0)
            {
                returns.Add((prices[i] / prices[i - 1]) - 1m);
            }
        }

        return returns.Count > 0 ? (decimal)returns.Select(r => (double)r).StandardDeviation() : 0m;
    }

    // ML feature calculations
    private decimal CalculateOrderBookImbalance(OrderBook orderBook)
    {
        var bidDepth = CalculateDepth(orderBook, Side.Buy, 5);
        var askDepth = CalculateDepth(orderBook, Side.Sell, 5);
        var totalDepth = bidDepth + askDepth;

        if (totalDepth <= 0)
            return 0m;

        // Return imbalance: positive = more bids, negative = more asks
        return (bidDepth - askDepth) / totalDepth;
    }

    private decimal CalculateDepth(OrderBook orderBook, Side side, int levels)
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

    private decimal CalculatePriceAcceleration(MomentumContext context)
    {
        if (context.PriceHistory.Count < 6)
            return 0m;

        var prices = context.PriceHistory.TakeLast(6).ToArray();

        // Calculate second derivative (acceleration)
        var velocity1 = prices[3] - prices[0]; // 3-period velocity
        var velocity2 = prices[5] - prices[2]; // Later 3-period velocity

        if (prices[0] <= 0)
            return 0m;

        return ((velocity2 - velocity1) / prices[0]) * 1000m; // Scale for readability
    }

    private decimal CalculateVolumePriceDivergence(MomentumContext context)
    {
        if (context.PriceHistory.Count < 10 || context.VolumeHistory.Count < 10)
            return 0m;

        // Price direction
        var priceChange = context.PriceHistory.Last() - context.PriceHistory[^10];
        var priceDirection = Math.Sign(priceChange);

        // Volume trend
        var recentVolume = context.VolumeHistory.TakeLast(5).Average();
        var olderVolume = context.VolumeHistory.Skip(Math.Max(0, context.VolumeHistory.Count - 10)).Take(5).Average();
        var volumeChange = recentVolume - olderVolume;
        var volumeDirection = Math.Sign(volumeChange);

        // Convergence = same direction, divergence = opposite direction
        return priceDirection * volumeDirection * Math.Abs(priceChange) / context.PriceHistory.Last();
    }

    private decimal CalculateTimeDecay(MomentumContext context)
    {
        var timeSinceLastSignal = TimestampUtils.GetTimestampMicros() - context.LastSignalTime;
        var decayPeriod = TimeSpan.FromMinutes(1).Ticks / 10; // Convert to microseconds

        return Math.Min(1m, (decimal)timeSinceLastSignal / decayPeriod);
    }

    private decimal ClassifyVolatilityRegime(MomentumContext context)
    {
        if (context.PriceHistory.Count < 30)
            return 0m;

        var recentVol = CalculateVolatility(context.PriceHistory.TakeLast(10).ToList());
        var historicalVol = CalculateVolatility(context.PriceHistory.TakeLast(30).ToList());

        if (historicalVol <= 0)
            return 0m;

        var volRatio = recentVol / historicalVol;

        // Return regime indicator: +1 = high vol, 0 = normal, -1 = low vol
        return volRatio switch
        {
            > 1.5m => 1m,
            < 0.7m => -1m,
            _ => 0m
        };
    }

    // Position and PnL management
    private void UpdatePositionTracking(MomentumContext context,
        (SignalDirection direction, decimal confidence, decimal size) signal, OrderIntent? order)
    {
        if (order == null) return;

        var orderSize = signal.direction == SignalDirection.Long ? signal.size : -signal.size;

        context.CurrentPosition += orderSize;
        context.LastEntryPrice = PriceTicksToDecimal(order.Value.PriceTicks);
        context.LastEntryTime = TimestampUtils.GetTimestampMicros();
        context.LastSignalTime = context.LastEntryTime;
    }

    private void UpdatePnLTracking(MomentumContext context, OrderIntent? closeOrder)
    {
        if (closeOrder == null) return;

        var realizedPnL = CalculateUnrealizedPnL(context);

        lock (_statsLock)
        {
            _totalPnL += realizedPnL;

            if (realizedPnL > 0)
            {
                Interlocked.Increment(ref _correctPredictions);
            }
        }

        // Reset position
        context.CurrentPosition = 0m;
        context.LastEntryPrice = 0m;
    }

    private decimal CalculateKellySize(MomentumContext context, decimal confidence)
    {
        // Simplified Kelly criterion
        var winRate = context.WinRate;
        var avgWin = context.AverageWin;
        var avgLoss = context.AverageLoss;

        if (avgLoss <= 0)
            return 0.5m; // Default small size

        var kellyFraction = (winRate * avgWin - (1 - winRate) * avgLoss) / avgWin;
        kellyFraction = Math.Max(0.01m, Math.Min(0.25m, kellyFraction)); // Limit to 1-25%

        // Scale by confidence
        return kellyFraction * confidence * _maxPositionSize;
    }

    // Model weight updates (simplified online learning)
    private void UpdateModelWeights(MomentumContext context, (decimal totalScore, SignalDirection direction) signal)
    {
        // This is a simplified version of online learning
        // In a real implementation, you'd use proper ML algorithms

        var performance = CalculateRecentPerformance(context);
        var learningRate = 0.01m;

        if (performance > 0) // Good recent performance
        {
            // Don't change weights much
        }
        else if (performance < 0) // Poor recent performance
        {
            // Adjust weights slightly
            _priceFeatureWeight *= (1m - learningRate);
            _volumeFeatureWeight *= (1m + learningRate * 0.5m);
            _volatilityFeatureWeight *= (1m + learningRate * 0.5m);
        }

        // Ensure weights sum to reasonable total
        var totalWeight = _priceFeatureWeight + _volumeFeatureWeight + _volatilityFeatureWeight + _orderBookFeatureWeight;
        if (totalWeight > 1.2m || totalWeight < 0.8m)
        {
            _priceFeatureWeight = 0.4m;
            _volumeFeatureWeight = 0.3m;
            _volatilityFeatureWeight = 0.2m;
            _orderBookFeatureWeight = 0.1m;
        }
    }

    private decimal CalculateRecentPerformance(MomentumContext context)
    {
        // Calculate performance over recent trades
        return context.RecentPnL.Count > 0 ? context.RecentPnL.TakeLast(10).Sum() : 0m;
    }

    // Context management
    private MomentumContext GetOrCreateMomentumContext(int symbolId)
    {
        lock (_contextLock)
        {
            if (!_symbolContexts.TryGetValue(symbolId, out var context))
            {
                context = new MomentumContext(symbolId);
                _symbolContexts[symbolId] = context;
            }
            return context;
        }
    }

    private void UpdateMomentumContext(MomentumContext context, MarketDataEvent marketEvent, OrderBook orderBook)
    {
        lock (context.UpdateLock)
        {
            var currentPrice = PriceTicksToDecimal(orderBook.GetMidPriceTicks());

            context.PriceHistory.Add(currentPrice);
            if (context.PriceHistory.Count > 100) // Keep rolling window
            {
                context.PriceHistory.RemoveAt(0);
            }

            if (marketEvent.Kind == EventKind.Trade)
            {
                var volume = PriceTicksToDecimal(orderBook.GetBestAsk().quantity);
                context.VolumeHistory.Add(volume);

                if (context.VolumeHistory.Count > 100)
                {
                    context.VolumeHistory.RemoveAt(0);
                }

                // Update tick direction
                if (context.LastPrice > 0)
                {
                    context.LastTickDirection = currentPrice > context.LastPrice ? 1m :
                                             currentPrice < context.LastPrice ? -1m : 0m;
                    context.LastTickSize = Math.Abs(currentPrice - context.LastPrice);
                }

                context.LastPrice = currentPrice;
            }

            context.LastUpdateTime = TimestampUtils.GetTimestampMicros();
        }
    }

    // Helper methods
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private decimal? CalculateMidPrice(OrderBook orderBook)
    {
        var bestBid = orderBook.GetBestBid();
        var bestAsk = orderBook.GetBestAsk();

        if (bestBid.priceTicks > 0 && bestAsk.priceTicks > 0)
        {
            return (PriceTicksToDecimal(bestBid.priceTicks) + PriceTicksToDecimal(bestAsk.priceTicks)) / 2m;
        }

        return null;
    }

    private decimal CalculateSpread(OrderBook orderBook)
    {
        var bestBid = orderBook.GetBestBid();
        var bestAsk = orderBook.GetBestAsk();

        if (bestBid.priceTicks > 0 && bestAsk.priceTicks > 0)
        {
            return PriceTicksToDecimal(bestAsk.priceTicks) - PriceTicksToDecimal(bestBid.priceTicks);
        }

        return 0m;
    }

    public Task StartAsync()
    {
        State = AdvancedStrategyState.Running;
        _logger.LogInformation("ML Momentum strategy started");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        State = AdvancedStrategyState.Stopped;
        _logger.LogInformation("ML Momentum strategy stopped");
        return Task.CompletedTask;
    }

    public StrategyStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            var successRate = _signalsExecuted > 0 ? (decimal)_correctPredictions / _signalsExecuted : 0m;

            return new StrategyStatistics
            {
                StrategyName = Name,
                TotalSignals = _signalsGenerated,
                ExecutedSignals = _signalsExecuted,
                SuccessRate = successRate,
                TotalPnL = _totalPnL,
                Sharpe = CalculateSharpeRatio(),
                MaxDrawdown = 0m, // TODO: Implement
                ActivePositions = _symbolContexts.Values.Sum(c => (long)Math.Abs(c.CurrentPosition))
            };
        }
    }

    private decimal CalculateSharpeRatio()
    {
        if (_signalsExecuted == 0) return 0m;

        var avgReturn = _totalPnL / _signalsExecuted;
        var riskFreeRate = 0.02m / 365m;

        // Calculate return volatility from context
        var allPnL = _symbolContexts.Values.SelectMany(c => c.RecentPnL).ToList();
        var returnVol = allPnL.Count > 1 ? (decimal)allPnL.Select(p => (double)p).StandardDeviation() : 0.01m;

        return returnVol > 0 ? (avgReturn - riskFreeRate) / returnVol : 0m;
    }

    private long GenerateOrderId() => TimestampUtils.GetTimestampMicros();
    private decimal PriceTicksToDecimal(long ticks) => ticks * 0.01m;
    private long DecimalToPriceTicks(decimal price) => (long)(price * 100);
    private long DecimalToQuantityTicks(decimal quantity) => (long)(quantity * 100_000_000);
}

// Supporting data structures
public class MomentumContext
{
    public int SymbolId { get; }
    public List<decimal> PriceHistory { get; } = new();
    public List<decimal> VolumeHistory { get; } = new();
    public List<decimal> RecentPnL { get; } = new();

    public decimal FastEMA { get; set; }
    public decimal SlowEMA { get; set; }
    public decimal LastPrice { get; set; }
    public decimal LastTickDirection { get; set; }
    public decimal LastTickSize { get; set; }

    public decimal CurrentPosition { get; set; }
    public decimal LastEntryPrice { get; set; }
    public long LastEntryTime { get; set; }
    public long LastSignalTime { get; set; }
    public long LastUpdateTime { get; set; }

    // Performance tracking
    public decimal WinRate { get; set; } = 0.55m;
    public decimal AverageWin { get; set; } = 0.01m;
    public decimal AverageLoss { get; set; } = 0.008m;

    public readonly object UpdateLock = new();

    public MomentumContext(int symbolId)
    {
        SymbolId = symbolId;
    }
}

public record TechnicalSignals
{
    public decimal EmaCrossSignal { get; set; }
    public decimal PriceMomentum { get; set; }
    public decimal VolumeMomentum { get; set; }
    public decimal VolatilitySignal { get; set; }
}

public record MLFeatures
{
    public decimal OrderBookImbalance { get; set; }
    public decimal PriceAcceleration { get; set; }
    public decimal VolumePriceDivergence { get; set; }
    public decimal TickDirection { get; set; }
    public decimal TickSize { get; set; }
    public decimal TimeDecay { get; set; }
    public decimal VolatilityRegime { get; set; }
}

public enum SignalDirection
{
    None,
    Long,
    Short
}
