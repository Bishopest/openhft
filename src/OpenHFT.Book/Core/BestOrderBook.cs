using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Book.Core;

/// <summary>
/// Represents an L1 (Top of Book) order book, optimized for tracking only the best bid and ask.
/// This class is designed for extremely low-latency access and updates, suitable for 'bookTicker' style feeds.
/// It uses volatile fields for thread-safe reads without locking.
/// </summary>
public class BestOrderBook
{
    private readonly Instrument _instrument;
    private readonly ILogger<BestOrderBook>? _logger;

    // Best Bid
    private long _bestBidPrice;
    private long _bestBidQuantity;

    // Best Ask
    private long _bestAskPrice;
    private long _bestAskQuantity;

    // State
    private long _lastUpdateTimestamp;
    private long _updateCount;

    public BestOrderBook(Instrument instrument, ILogger<BestOrderBook>? logger = null)
    {
        _instrument = instrument;
        _logger = logger;
    }

    public string Symbol => _instrument.Symbol;
    public int InstrumentId => _instrument.InstrumentId;
    public long LastUpdateTimestamp => _lastUpdateTimestamp;
    public long UpdateCount => _updateCount;

    /// <summary>
    /// Applies a market data event to the L1 order book.
    /// This method is optimized for top-of-book updates.
    /// </summary>
    /// <param name="mdEvent">The market data event, expected to be a top-level update.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyEvent(in MarketDataEvent mdEvent)
    {
        if (mdEvent.InstrumentId != InstrumentId)
        {
            _logger?.LogWarningWithCaller($"Instrument ID mismatch. Expected: {InstrumentId}, Got: {mdEvent.InstrumentId}");
            return;
        }

        if (mdEvent.UpdateCount != 2)
        {
            _logger?.LogWarningWithCaller($"Invalid update count. Expected: 2, Got: {mdEvent.UpdateCount}");
            return;
        }

        _lastUpdateTimestamp = mdEvent.Timestamp;
        _updateCount++;

        // The event contains a batch of updates. We need to find the best bid and ask from it.
        // For a bookTicker feed, this will typically be just one bid and one ask.
        for (int i = 0; i < mdEvent.UpdateCount; i++)
        {
            var update = mdEvent.Updates[i];

            if (update.Side == Side.Buy)
            {
                _bestBidPrice = update.PriceTicks;
                _bestBidQuantity = update.Quantity;
            }
            else // Side.Sell
            {
                _bestAskPrice = update.PriceTicks;
                _bestAskQuantity = update.Quantity;
            }
        }
    }

    /// <summary>
    /// Gets the best bid price and quantity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (long priceTicks, long quantity) GetBestBid()
    {
        return (_bestBidPrice, _bestBidQuantity);
    }

    /// <summary>
    /// Gets the best ask price and quantity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (long priceTicks, long quantity) GetBestAsk()
    {
        return (_bestAskPrice, _bestAskQuantity);
    }

    /// <summary>
    /// Gets the current spread in ticks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetSpreadTicks()
    {
        var bid = _bestBidPrice;
        var ask = _bestAskPrice;
        return (bid > 0 && ask > 0) ? ask - bid : 0;
    }

    /// <summary>
    /// Gets the mid price in ticks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetMidPriceTicks()
    {
        var bid = _bestBidPrice;
        var ask = _bestAskPrice;
        return (bid > 0 && ask > 0) ? (bid + ask) / 2 : 0;
    }

    /// <summary>
    /// Clears the book state.
    /// </summary>
    public void Clear()
    {
        _bestBidPrice = 0;
        _bestBidQuantity = 0;
        _bestAskPrice = 0;
        _bestAskQuantity = 0;
        _lastUpdateTimestamp = 0;
        _updateCount = 0;
    }

    /// <summary>
    /// Returns a string representation of the top of the book for terminal logging.
    /// </summary>
    /// <returns>A formatted string representing the L1 order book state.</returns>
    public string ToTerminalString()
    {
        var sb = new StringBuilder();
        var (bidPriceTicks, bidQty) = GetBestBid();
        var (askPriceTicks, askQty) = GetBestAsk();

        // Assuming price ticks are convertible to decimal by dividing by 100.
        var bidPrice = bidPriceTicks / 100.0m;
        var askPrice = askPriceTicks / 100.0m;
        var spread = (bidPrice > 0 && askPrice > 0) ? askPrice - bidPrice : 0m;

        var bidSizeStr = bidQty > 0 ? bidQty.ToString("N0") : " - ";
        var bidPriceStr = bidPrice > 0 ? bidPrice.ToString("F2") : " - ";
        var askPriceStr = askPrice > 0 ? askPrice.ToString("F2") : " - ";
        var askSizeStr = askQty > 0 ? askQty.ToString("N0") : " - ";
        var spreadStr = spread > 0 ? $"[{spread:F2}]" : "";

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

        sb.Append($"| {bidSizeStr.PadLeft(sizeWidth)} ");
        sb.Append($"| {bidPriceStr.PadLeft(priceWidth)} ");
        sb.Append($"| {spreadStr.PadLeft(spreadWidth)} ");
        sb.Append($"| {askPriceStr.PadRight(priceWidth)} ");
        sb.Append($"| {askSizeStr.PadRight(sizeWidth)} |");
        sb.AppendLine();

        sb.AppendLine(new string('-', 2 * (sizeWidth + priceWidth) + spreadWidth + 7));
        sb.AppendLine($"Symbol: {Symbol}, Last Update: {DateTimeOffset.FromUnixTimeMilliseconds(LastUpdateTimestamp):HH:mm:ss.fff}");

        return sb.ToString();
    }
}
