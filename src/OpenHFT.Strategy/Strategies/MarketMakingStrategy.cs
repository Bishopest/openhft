using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Book.Core;
using OpenHFT.Strategy.Interfaces;

namespace OpenHFT.Strategy.Strategies;

/// <summary>
/// Market Making strategy that provides liquidity by quoting both sides
/// Features:
/// - Adaptive spread based on volatility and inventory
/// - Inventory management with target position
/// - Order Flow Imbalance (OFI) based adjustments
/// - Risk-aware position sizing
/// </summary>
public class MarketMakingStrategy : BaseStrategy
{
    private readonly ILogger<MarketMakingStrategy> _logger;

    // Configuration parameters
    private decimal _baseSpreadTicks = 2;
    private decimal _maxPosition = 1000;
    private decimal _targetInventory = 0;
    private decimal _inventoryDecayRate = 0.1m;
    private decimal _quoteSizeTicks = 100;
    private decimal _maxOrderSize = 100;
    private int _requoteThresholdTicks = 1;
    private double _ofiThreshold = 0.1;
    private int _depthLevels = 5;

    // State tracking
    private readonly Dictionary<string, decimal> _positions = new();
    private readonly Dictionary<string, QuoteState> _quoteStates = new();
    private readonly Dictionary<long, ActiveQuote> _activeQuotes = new();
    private long _nextClientOrderId = 1;

    // Market state
    private readonly Dictionary<string, MarketState> _marketStates = new();

    public MarketMakingStrategy(ILogger<MarketMakingStrategy> logger) : base("MarketMaking")
    {
        _logger = logger;
    }

    public override async Task InitializeAsync(StrategyConfiguration configuration)
    {
        await base.InitializeAsync(configuration);

        // Load configuration parameters
        if (configuration.Parameters.TryGetValue("BaseSpreadTicks", out var baseSpread))
            _baseSpreadTicks = Convert.ToDecimal(baseSpread);

        if (configuration.Parameters.TryGetValue("MaxPosition", out var maxPos))
            _maxPosition = Convert.ToDecimal(maxPos);

        if (configuration.Parameters.TryGetValue("QuoteSizeTicks", out var quoteSize))
            _quoteSizeTicks = Convert.ToDecimal(quoteSize);

        if (configuration.Parameters.TryGetValue("MaxOrderSize", out var maxOrderSize))
            _maxOrderSize = Convert.ToDecimal(maxOrderSize);

        // Initialize state for each symbol
        foreach (var symbol in Symbols)
        {
            _positions[symbol] = 0;
            _quoteStates[symbol] = new QuoteState();
            _marketStates[symbol] = new MarketState();
        }

        _logger.LogInformation("MarketMaking strategy initialized with {SymbolCount} symbols, BaseSpread={BaseSpread}, MaxPosition={MaxPosition}",
            Symbols.Count, _baseSpreadTicks, _maxPosition);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override IEnumerable<OrderIntent> OnMarketData(MarketDataEvent marketDataEvent, OrderBook orderBook)
    {
        if (!IsEnabled || !IsRunning) return Enumerable.Empty<OrderIntent>();

        var symbol = SymbolUtils.GetSymbol(marketDataEvent.InstrumentId);
        if (!Symbols.Contains(symbol)) return Enumerable.Empty<OrderIntent>();

        try
        {
            // Update market state
            UpdateMarketState(symbol, orderBook, marketDataEvent.Timestamp);

            var orders = new List<OrderIntent>();

            // Check if we should requote
            if (ShouldRequote(symbol, orderBook))
            {
                // Cancel existing quotes
                orders.AddRange(CancelActiveQuotes(symbol));

                // Generate new quotes
                orders.AddRange(GenerateQuotes(symbol, orderBook, marketDataEvent.Timestamp));
            }

            IncrementMessageCount();
            return orders;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing market data for {Symbol}", symbol);
            return Enumerable.Empty<OrderIntent>();
        }
    }

    private void UpdateMarketState(string symbol, OrderBook orderBook, long timestamp)
    {
        var marketState = _marketStates[symbol];
        var (bidPrice, bidQty) = orderBook.GetBestBid();
        var (askPrice, askQty) = orderBook.GetBestAsk();

        marketState.LastBidPrice = bidPrice;
        marketState.LastAskPrice = askPrice;
        marketState.LastBidQuantity = bidQty;
        marketState.LastAskQuantity = askQty;
        marketState.LastUpdateTimestamp = timestamp;

        // Calculate OFI (Order Flow Imbalance)
        marketState.OrderFlowImbalance = orderBook.CalculateOrderFlowImbalance(_depthLevels);

        // Calculate mid price
        if (bidPrice > 0 && askPrice > 0)
        {
            var newMidPrice = (bidPrice + askPrice) / 2;
            if (marketState.MidPrice > 0)
            {
                var priceDelta = Math.Abs(newMidPrice - marketState.MidPrice);
                marketState.VolatilityEMA = 0.9 * marketState.VolatilityEMA + 0.1 * (double)priceDelta;
            }
            marketState.MidPrice = newMidPrice;
        }

        // Update spread
        marketState.Spread = bidPrice > 0 && askPrice > 0 ? askPrice - bidPrice : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldRequote(string symbol, OrderBook orderBook)
    {
        var quoteState = _quoteStates[symbol];
        var marketState = _marketStates[symbol];

        // Always quote if we don't have active quotes
        if (quoteState.BidOrderId == 0 && quoteState.AskOrderId == 0)
            return true;

        // Check if market moved significantly
        var (currentBid, _) = orderBook.GetBestBid();
        var (currentAsk, _) = orderBook.GetBestAsk();

        if (Math.Abs(currentBid - quoteState.LastBidPrice) >= _requoteThresholdTicks ||
            Math.Abs(currentAsk - quoteState.LastAskPrice) >= _requoteThresholdTicks)
        {
            return true;
        }

        // Check OFI threshold
        if (Math.Abs(marketState.OrderFlowImbalance) > _ofiThreshold)
        {
            return true;
        }

        // Check inventory threshold
        var position = _positions[symbol];
        if (Math.Abs(position) > _maxPosition * 0.8m) // 80% of max position
        {
            return true;
        }

        return false;
    }

    private IEnumerable<OrderIntent> CancelActiveQuotes(string symbol)
    {
        var quoteState = _quoteStates[symbol];

        if (quoteState.BidOrderId > 0)
        {
            yield return new OrderIntent(
                clientOrderId: quoteState.BidOrderId,
                type: OrderTypeEnum.Limit, // Cancel is handled by gateway
                side: Side.Buy,
                priceTicks: 0, // Cancel order
                quantity: 0,
                timestampIn: TimestampUtils.GetTimestampMicros(),
                symbolId: SymbolUtils.GetSymbolId(symbol)
            );
            quoteState.BidOrderId = 0;
        }

        if (quoteState.AskOrderId > 0)
        {
            yield return new OrderIntent(
                clientOrderId: quoteState.AskOrderId,
                type: OrderTypeEnum.Limit,
                side: Side.Sell,
                priceTicks: 0, // Cancel order
                quantity: 0,
                timestampIn: TimestampUtils.GetTimestampMicros(),
                symbolId: SymbolUtils.GetSymbolId(symbol)
            );
            quoteState.AskOrderId = 0;
        }
    }

    private IEnumerable<OrderIntent> GenerateQuotes(string symbol, OrderBook orderBook, long timestamp)
    {
        var position = _positions[symbol];
        var marketState = _marketStates[symbol];
        var quoteState = _quoteStates[symbol];

        var (marketBid, _) = orderBook.GetBestBid();
        var (marketAsk, _) = orderBook.GetBestAsk();

        if (marketBid == 0 || marketAsk == 0) yield break;

        // Calculate adaptive spread
        var adaptiveSpread = CalculateAdaptiveSpread(marketState, position);
        var midPrice = (marketBid + marketAsk) / 2;

        // Calculate inventory adjustment
        var inventoryAdjustment = CalculateInventoryAdjustment(position);

        // Calculate quote prices with inventory skew
        var bidPrice = midPrice - adaptiveSpread / 2 - inventoryAdjustment;
        var askPrice = midPrice + adaptiveSpread / 2 - inventoryAdjustment;

        // Ensure we're improving the market or staying competitive
        bidPrice = Math.Min(bidPrice, marketBid - 1); // Inside or improve
        askPrice = Math.Max(askPrice, marketAsk + 1); // Inside or improve

        // Calculate position-adjusted sizes
        var bidSize = CalculateOrderSize(symbol, Side.Buy, position);
        var askSize = CalculateOrderSize(symbol, Side.Sell, position);

        // Generate bid quote
        if (bidSize > 0 && bidPrice > 0)
        {
            var bidOrderId = Interlocked.Increment(ref _nextClientOrderId);
            quoteState.BidOrderId = bidOrderId;
            quoteState.LastBidPrice = bidPrice;

            _activeQuotes[bidOrderId] = new ActiveQuote
            {
                Symbol = symbol,
                Side = Side.Buy,
                Price = bidPrice,
                Quantity = bidSize,
                Timestamp = timestamp
            };

            yield return new OrderIntent(
                clientOrderId: bidOrderId,
                type: OrderTypeEnum.Limit,
                side: Side.Buy,
                priceTicks: bidPrice,
                quantity: bidSize,
                timestampIn: timestamp,
                symbolId: SymbolUtils.GetSymbolId(symbol)
            );
        }

        // Generate ask quote
        if (askSize > 0 && askPrice > 0)
        {
            var askOrderId = Interlocked.Increment(ref _nextClientOrderId);
            quoteState.AskOrderId = askOrderId;
            quoteState.LastAskPrice = askPrice;

            _activeQuotes[askOrderId] = new ActiveQuote
            {
                Symbol = symbol,
                Side = Side.Sell,
                Price = askPrice,
                Quantity = askSize,
                Timestamp = timestamp
            };

            yield return new OrderIntent(
                clientOrderId: askOrderId,
                type: OrderTypeEnum.Limit,
                side: Side.Sell,
                priceTicks: askPrice,
                quantity: bidSize,
                timestampIn: timestamp,
                symbolId: SymbolUtils.GetSymbolId(symbol)
            );
        }

        IncrementOrderCount();
    }

    private long CalculateAdaptiveSpread(MarketState marketState, decimal position)
    {
        // Base spread
        var adaptiveSpread = _baseSpreadTicks;

        // Increase spread with volatility
        adaptiveSpread *= (decimal)(1.0 + marketState.VolatilityEMA * 2);

        // Increase spread with inventory risk
        var inventoryRatio = Math.Abs(position) / _maxPosition;
        adaptiveSpread *= (1 + inventoryRatio * 0.5m);

        // Adjust for OFI - widen spread when market is imbalanced
        adaptiveSpread *= (decimal)(1.0 + Math.Abs(marketState.OrderFlowImbalance) * 0.3);

        return Math.Max(1, (long)Math.Ceiling(adaptiveSpread)); // Minimum 1 tick spread
    }

    private long CalculateInventoryAdjustment(decimal position)
    {
        // Skew quotes based on inventory to target neutral position
        var inventoryRatio = position / _maxPosition;
        var adjustmentTicks = inventoryRatio * _baseSpreadTicks * 0.5m; // Max adjustment = half spread

        return (long)adjustmentTicks;
    }

    private long CalculateOrderSize(string symbol, Side side, decimal position)
    {
        var baseSize = _quoteSizeTicks;

        // Reduce size when approaching position limits
        var inventoryRatio = Math.Abs(position) / _maxPosition;
        if (inventoryRatio > 0.7m)
        {
            baseSize *= (1m - inventoryRatio);
        }

        // Reduce size for the side that would increase inventory risk
        if ((side == Side.Buy && position > _maxPosition * 0.5m) ||
            (side == Side.Sell && position < -_maxPosition * 0.5m))
        {
            baseSize *= 0.5m; // Half size for risky direction
        }

        return Math.Max(1, Math.Min((long)baseSize, (long)_maxOrderSize));
    }

    public override void OnOrderAck(OrderAck orderAck)
    {
        if (orderAck.Kind == AckKind.Rejected || orderAck.Kind == AckKind.Cancelled)
        {
            _activeQuotes.Remove(orderAck.ClientOrderId);
        }

        base.OnOrderAck(orderAck);
    }

    public override void OnFill(FillEvent fillEvent)
    {
        if (_activeQuotes.TryGetValue(fillEvent.ClientOrderId, out var quote))
        {
            var symbol = quote.Symbol;
            var fillQuantity = quote.Side == Side.Buy ? fillEvent.Quantity : -fillEvent.Quantity;

            // Update position
            _positions[symbol] = _positions.GetValueOrDefault(symbol) + fillQuantity;

            // Update realized PnL (simplified)
            var fillValue = fillEvent.PriceTicks * fillEvent.Quantity;
            State.RealizedPnL += quote.Side == Side.Buy ? -fillValue : fillValue;

            if (fillEvent.IsFullFill)
            {
                _activeQuotes.Remove(fillEvent.ClientOrderId);

                // Clear quote state
                var quoteState = _quoteStates[symbol];
                if (quoteState.BidOrderId == fillEvent.ClientOrderId)
                    quoteState.BidOrderId = 0;
                if (quoteState.AskOrderId == fillEvent.ClientOrderId)
                    quoteState.AskOrderId = 0;
            }

            _logger.LogInformation("Fill: {Symbol} {Side} {Quantity}@{Price}, Position: {Position}",
                symbol, quote.Side, fillEvent.Quantity, fillEvent.PriceTicks, _positions[symbol]);
        }

        base.OnFill(fillEvent);
    }

    public override StrategyState GetState()
    {
        var state = base.GetState();
        state.Positions = new Dictionary<string, decimal>(_positions);
        state.ActiveOrders = _quoteStates.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value.BidOrderId > 0 ? 1 : 0) + (kvp.Value.AskOrderId > 0 ? 1 : 0)
        );

        // Add strategy-specific metrics
        state.Metrics["ActiveQuotes"] = _activeQuotes.Count;
        state.Metrics["TotalPosition"] = _positions.Values.Sum();
        state.Metrics["MaxPosition"] = _maxPosition;

        return state;
    }

    private class QuoteState
    {
        public long BidOrderId;
        public long AskOrderId;
        public long LastBidPrice;
        public long LastAskPrice;
    }

    private class ActiveQuote
    {
        public string Symbol = "";
        public Side Side;
        public long Price;
        public long Quantity;
        public long Timestamp;
    }

    private class MarketState
    {
        public long MidPrice;
        public long LastBidPrice;
        public long LastAskPrice;
        public long LastBidQuantity;
        public long LastAskQuantity;
        public long Spread;
        public double VolatilityEMA;
        public double OrderFlowImbalance;
        public long LastUpdateTimestamp;
    }
}
