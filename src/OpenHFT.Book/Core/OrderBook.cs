using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Book.Models;
using OpenHFT.Core.Instruments;
using System.Text;

namespace OpenHFT.Book.Core;

/// <summary>
/// High-performance order book implementation
/// Supports both L2 (price aggregated) and L3 (individual orders) views
/// Optimized for single-threaded access with minimal allocations
/// </summary>
public class OrderBook
{
    private readonly ILogger<OrderBook>? _logger;
    private readonly Instrument _instrument;
    private readonly BookSide _bids;
    private readonly BookSide _asks;

    // Book state tracking
    private long _lastSequence;
    private long _lastUpdateTimestamp;
    private long _snapshotSequence;

    // Statistics
    private long _updateCount;
    private long _tradeCount;

    // L3 simulation support
    private readonly bool _enableL3;
    private long _nextOrderId = 1;

    public OrderBook(Instrument instrument, ILogger<OrderBook>? logger = null, bool enableL3 = false)
    {
        _logger = logger;
        _instrument = instrument;
        _enableL3 = enableL3;

        _bids = new BookSide(Side.Buy);
        _asks = new BookSide(Side.Sell);

        _lastSequence = 0;
        _lastUpdateTimestamp = 0;
        _snapshotSequence = 0;
        _updateCount = 0;
        _tradeCount = 0;
    }

    public string Symbol => _instrument.Symbol;
    public int InstrumentId => _instrument.InstrumentId;
    public ExchangeEnum SourceExchange => _instrument.SourceExchange;
    public long LastSequence => _lastSequence;
    public long LastUpdateTimestamp => _lastUpdateTimestamp;
    public long UpdateCount => _updateCount;
    public long TradeCount => _tradeCount;

    /// <summary>
    /// Apply a market data event to the order book
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ApplyEvent(in MarketDataEvent mdEvent)
    {
        // Validate symbol
        if (mdEvent.InstrumentId != InstrumentId)
        {
            _logger?.LogWarningWithCaller($"Symbol mismatch: expected {Symbol}, got {mdEvent.InstrumentId}");
            return false;
        }

        // Check sequence order (basic gap detection)
        if (_lastSequence > 0 && mdEvent.Sequence < _lastSequence)
        {
            _logger?.LogWarningWithCaller($"Out of order sequence for {Symbol}: current={_lastSequence}, received={mdEvent.Sequence}");
            return false;
        }

        // Update book state
        _lastSequence = mdEvent.Sequence;
        _lastUpdateTimestamp = mdEvent.Timestamp;
        _updateCount++;

        // Apply the event based on its kind
        switch (mdEvent.Kind)
        {
            case EventKind.Add:
            case EventKind.Update:
                UpdateLevel(mdEvent.Side, mdEvent.PriceTicks, mdEvent.Quantity, mdEvent.Sequence, mdEvent.Timestamp);
                return true;

            case EventKind.Delete:
                UpdateLevel(mdEvent.Side, mdEvent.PriceTicks, 0, mdEvent.Sequence, mdEvent.Timestamp);
                return true;

            case EventKind.Trade:
                ProcessTrade(mdEvent);
                return true;

            case EventKind.Snapshot:
                ProcessSnapshot(mdEvent);
                return true;

            default:
                _logger?.LogWarning("Unknown event kind: {Kind}", mdEvent.Kind);
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateLevel(Side side, long priceTicks, long quantity, long sequence, long timestamp)
    {
        var bookSide = side == Side.Buy ? _bids : _asks;
        bookSide.UpdateLevel(priceTicks, quantity, sequence, timestamp);
    }

    private void ProcessTrade(in MarketDataEvent mdEvent)
    {
        _tradeCount++;
        _logger?.LogDebug("Trade: {Symbol} {Side} {Price}@{Quantity}", Symbol, mdEvent.Side, mdEvent.PriceTicks, mdEvent.Quantity);
    }

    private void ProcessSnapshot(in MarketDataEvent mdEvent)
    {
        // Snapshot processing would typically involve clearing the book
        // and rebuilding from snapshot data
        _snapshotSequence = mdEvent.Sequence;
        _logger?.LogInformation("Processed snapshot for {Symbol} at sequence {Sequence}", Symbol, mdEvent.Sequence);
    }

    /// <summary>
    /// Get the best bid price and quantity
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (long priceTicks, long quantity) GetBestBid()
    {
        var bestBid = _bids.GetBestLevel();
        return bestBid != null ? (bestBid.PriceTicks, bestBid.TotalQuantity) : (0, 0);
    }

    /// <summary>
    /// Get the best ask price and quantity
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (long priceTicks, long quantity) GetBestAsk()
    {
        var bestAsk = _asks.GetBestLevel();
        return bestAsk != null ? (bestAsk.PriceTicks, bestAsk.TotalQuantity) : (0, 0);
    }

    /// <summary>
    /// Get the current spread in ticks
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetSpreadTicks()
    {
        var (bidPrice, _) = GetBestBid();
        var (askPrice, _) = GetBestAsk();
        return bidPrice > 0 && askPrice > 0 ? askPrice - bidPrice : 0;
    }

    /// <summary>
    /// Get the mid price in ticks
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetMidPriceTicks()
    {
        var (bidPrice, _) = GetBestBid();
        var (askPrice, _) = GetBestAsk();
        return bidPrice > 0 && askPrice > 0 ? (bidPrice + askPrice) / 2 : 0;
    }

    /// <summary>
    /// Get top N levels for a side
    /// </summary>
    public IEnumerable<PriceLevel> GetTopLevels(Side side, int count)
    {
        var bookSide = side == Side.Buy ? _bids : _asks;
        return bookSide.GetTopLevels(count);
    }

    /// <summary>
    /// Calculate Order Flow Imbalance (OFI)
    /// Simplified version - in practice this would track changes over time
    /// </summary>
    public double CalculateOrderFlowImbalance(int levels = 5)
    {
        var bidDepth = _bids.GetDepth(levels);
        var askDepth = _asks.GetDepth(levels);

        var totalDepth = bidDepth + askDepth;
        if (totalDepth == 0) return 0.0;

        return (double)(bidDepth - askDepth) / totalDepth;
    }

    /// <summary>
    /// Get total depth for a side
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetDepth(Side side, int levels)
    {
        var bookSide = side == Side.Buy ? _bids : _asks;
        return bookSide.GetDepth(levels);
    }

    /// <summary>
    /// Check if the book is crossed (bid >= ask)
    /// </summary>
    public bool IsCrossed()
    {
        var (bidPrice, _) = GetBestBid();
        var (askPrice, _) = GetBestAsk();
        return bidPrice > 0 && askPrice > 0 && bidPrice >= askPrice;
    }

    /// <summary>
    /// Clear all levels in the book
    /// </summary>
    public void Clear()
    {
        _bids.Clear();
        _asks.Clear();
        _lastSequence = 0;
        _lastUpdateTimestamp = 0;
        _updateCount = 0;
        _tradeCount = 0;
    }

    /// <summary>
    /// Get book summary for monitoring/UI
    /// </summary>
    public BookSnapshot GetSnapshot(int levels = 10)
    {
        var bidLevels = _bids.GetTopLevels(levels).Select(l =>
            new BookLevel(l.PriceTicks, l.TotalQuantity, l.OrderCount)).ToArray();

        var askLevels = _asks.GetTopLevels(levels).Select(l =>
            new BookLevel(l.PriceTicks, l.TotalQuantity, l.OrderCount)).ToArray();

        return new BookSnapshot
        {
            Symbol = _instrument.Symbol,
            Timestamp = _lastUpdateTimestamp,
            Sequence = _lastSequence,
            Bids = bidLevels,
            Asks = askLevels,
            UpdateCount = _updateCount,
            TradeCount = _tradeCount
        };
    }

    /// <summary>
    /// Validate book integrity
    /// </summary>
    public bool ValidateIntegrity()
    {
        try
        {
            // Check if book is crossed
            if (IsCrossed())
            {
                _logger?.LogWarningWithCaller($"Book is crossed for {Symbol}");
                return false;
            }

            // Check for negative quantities (basic validation)
            foreach (var level in _bids.GetAllLevels())
            {
                if (level.TotalQuantity < 0)
                {
                    _logger?.LogWarningWithCaller($"Negative quantity in bids for {Symbol}: {level.PriceTicks}@{level.TotalQuantity}");
                    return false;
                }
            }

            foreach (var level in _asks.GetAllLevels())
            {
                if (level.TotalQuantity < 0)
                {
                    _logger?.LogWarningWithCaller($"Negative quantity in asks for {Symbol}: {level.PriceTicks}@{level.TotalQuantity}");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogErrorWithCaller(ex, $"Error validating book integrity for {Symbol}");
            return false;
        }
    }

    public override string ToString()
    {
        var (bidPrice, bidQty) = GetBestBid();
        var (askPrice, askQty) = GetBestAsk();
        return $"{Symbol}: {bidPrice}@{bidQty} | {askPrice}@{askQty} (Spread: {GetSpreadTicks()})";
    }

    /// <summary>
    /// Returns a string representation of the order book for terminal logging.
    /// </summary>
    /// <param name="levels">The number of price levels to display on each side.</param>
    /// <returns>A formatted string representing the order book state.</returns>
    public string ToTerminalString(int levels = 5)
    {
        var sb = new StringBuilder();
        var bids = GetTopLevels(Side.Buy, levels).ToArray();
        var asks = GetTopLevels(Side.Sell, levels).ToArray();

        var spread = GetSpreadTicks() / 100.0m;

        const int sizeWidth = 10;
        const int priceWidth = 10;
        const int spreadWidth = 8;

        sb.AppendLine(new string('-', 2 * (sizeWidth + priceWidth) + spreadWidth + 7));
        sb.Append($"| {"BID SIZE".PadLeft(sizeWidth)} ");
        sb.Append($"| {"BID PRICE".PadLeft(priceWidth)} ");
        sb.Append($"| {"SPREAD".PadLeft(spreadWidth)} ");
        sb.Append($"| {"ASK PRICE".PadRight(priceWidth)} ");
        sb.AppendLine($"| {"ASK SIZE".PadRight(sizeWidth)} |");
        sb.AppendLine(new string('-', 2 * (sizeWidth + priceWidth) + spreadWidth + 7));

        for (int i = 0; i < levels; i++)
        {
            var bid = i < bids.Length ? bids[i] : null;
            var ask = i < asks.Length ? asks[i] : null;

            // Assuming price ticks are convertible to decimal by dividing by 100.
            var bidPrice = bid != null ? (bid.PriceTicks / 100.0m).ToString("F2") : "";
            var bidSize = bid != null ? bid.TotalQuantity.ToString("N0") : "";

            var askPrice = ask != null ? (ask.PriceTicks / 100.0m).ToString("F2") : "";
            var askSize = ask != null ? ask.TotalQuantity.ToString("N0") : "";

            string spreadStr = "";
            if (i == 0 && spread > 0)
            {
                spreadStr = $"[{spread:F2}]";
            }

            sb.Append($"| {bidSize.PadLeft(sizeWidth)} ");
            sb.Append($"| {bidPrice.PadLeft(priceWidth)} ");
            sb.Append($"| {spreadStr.PadLeft(spreadWidth)} ");
            sb.Append($"| {askPrice.PadRight(priceWidth)} ");
            sb.Append($"| {askSize.PadRight(sizeWidth)} |");
            sb.AppendLine();
        }

        sb.AppendLine(new string('-', 2 * (sizeWidth + priceWidth) + spreadWidth + 7));
        sb.AppendLine($"Symbol: {Symbol}, Last Update: {DateTimeOffset.FromUnixTimeMilliseconds(LastUpdateTimestamp):HH:mm:ss.fff}, Seq: {LastSequence}");

        return sb.ToString();
    }
}

/// <summary>
/// Immutable snapshot of order book state
/// </summary>
public record BookSnapshot
{
    public string Symbol { get; init; } = "";
    public long Timestamp { get; init; }
    public long Sequence { get; init; }
    public BookLevel[] Bids { get; init; } = Array.Empty<BookLevel>();
    public BookLevel[] Asks { get; init; } = Array.Empty<BookLevel>();
    public long UpdateCount { get; init; }
    public long TradeCount { get; init; }
}

/// <summary>
/// Represents a price level in a snapshot
/// </summary>
public record BookLevel(long PriceTicks, long Quantity, long OrderCount);
