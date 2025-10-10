using System.Runtime.CompilerServices;
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

        _lastUpdateTimestamp = mdEvent.Timestamp;
        _updateCount++;

        if (mdEvent.Side == Side.Buy)
        {
            // Update Best Bid
            _bestBidPrice = mdEvent.PriceTicks;
            _bestBidQuantity = mdEvent.Quantity;
        }
        else // Side.Sell
        {
            // Update Best Ask
            _bestAskPrice = mdEvent.PriceTicks;
            _bestAskQuantity = mdEvent.Quantity;
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
}
