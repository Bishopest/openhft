using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Core.Models;

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
    private Price _bestBidPrice;
    private Quantity _bestBidQuantity;

    // Best Ask
    private Price _bestAskPrice;
    private Quantity _bestAskQuantity;

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
    public ExchangeEnum SourceExchange => _instrument.SourceExchange;
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
                _bestBidPrice = Price.FromDecimal(update.PriceTicks);
                _bestBidQuantity = Quantity.FromDecimal(update.Quantity);
            }
            else // Side.Sell
            {
                _bestAskPrice = Price.FromDecimal(update.PriceTicks);
                _bestAskQuantity = Quantity.FromDecimal(update.Quantity);
            }
        }
    }

    /// <summary>
    /// Gets the best bid price and quantity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Price price, Quantity quantity) GetBestBid()
    {
        return (_bestBidPrice, _bestBidQuantity);
    }

    /// <summary>
    /// Gets the best ask price and quantity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Price price, Quantity quantity) GetBestAsk()
    {
        return (_bestAskPrice, _bestAskQuantity);
    }

    /// <summary>
    /// Gets the current spread in ticks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Price GetSpread()
    {
        var bid = _bestBidPrice;
        var ask = _bestAskPrice;
        return (bid.ToTicks() > 0 && ask.ToTicks() > 0) ? ask - bid : Price.FromTicks(0);
    }

    /// <summary>
    /// Gets the mid price in ticks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Price GetMidPrice()
    {
        var bid = _bestBidPrice;
        var ask = _bestAskPrice;
        return (bid.ToTicks() > 0 && ask.ToTicks() > 0) ? Price.FromTicks((bid.ToTicks() + ask.ToTicks()) / 2) : Price.FromTicks(0);
    }

    /// <summary>
    /// Clears the book state.
    /// </summary>
    public void Clear()
    {
        _bestBidPrice = Price.FromTicks(0);
        _bestBidQuantity = Quantity.FromTicks(0);
        _bestAskPrice = Price.FromTicks(0);
        _bestAskQuantity = Quantity.FromTicks(0);
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
        var (bidPrice, bidQty) = GetBestBid();
        var (askPrice, askQty) = GetBestAsk();

        // Assuming price ticks are convertible to decimal by dividing by 100.
        var bidPriceDecimal = bidPrice.ToDecimal();
        var askPriceDecimal = askPrice.ToDecimal();
        var spread = (bidPriceDecimal > 0 && askPriceDecimal > 0) ? askPriceDecimal - bidPriceDecimal : 0m;

        var bidSizeStr = bidQty.ToTicks() > 0 ? bidQty.ToDecimal().ToString("N0") : " - ";
        var bidPriceStr = bidPriceDecimal > 0 ? bidPriceDecimal.ToString("F2") : " - ";
        var askPriceStr = askPriceDecimal > 0 ? askPriceDecimal.ToString("F2") : " - ";
        var askSizeStr = askQty.ToTicks() > 0 ? askQty.ToDecimal().ToString("N0") : " - ";
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
