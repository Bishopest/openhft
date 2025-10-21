using System;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Utils;
using OpenHFT.Processing;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

/// <summary>
/// A utility class to visualize the output of a QuotingEngine.
/// It subscribes to order book updates and monitors the state of mock quoters
/// to print a visual representation of the order book with the engine's quotes.
/// </summary>
public class QuotingDebugger
{
    private readonly ILogger _logger;
    private readonly Instrument _instrument;
    private readonly MarketDataManager _marketDataManager;
    private readonly QuotingEngine _quotingEngine;
    private readonly LogQuoter _bidQuoter;
    private readonly LogQuoter _askQuoter;

    private QuotePair? _lastDisplayedQuotePair;

    public QuotingDebugger(
        Instrument instrument,
        MarketDataManager marketDataManager,
        QuotingEngine quotingEngine,
        LogQuoter bidQuoter,
        LogQuoter askQuoter,
        ILogger logger)
    {
        _logger = logger;
        _instrument = instrument;
        _marketDataManager = marketDataManager;
        _quotingEngine = quotingEngine;
        _bidQuoter = bidQuoter;
        _askQuoter = askQuoter;
    }

    public void Start()
    {
        _marketDataManager.SubscribeOrderBook(_instrument.InstrumentId, $"QuotingDebugger_{_instrument.Symbol}", OnOrderBookUpdate);
        _logger.LogInformationWithCaller($"QuotingDebugger started for {_instrument.Symbol}. Waiting for market data...");
    }

    public void Stop()
    {
        _marketDataManager.UnsubscribeOrderBook(_instrument.InstrumentId, $"QuotingDebugger_{_instrument.Symbol}");
        _logger.LogInformationWithCaller($"QuotingDebugger stopped for {_instrument.Symbol}.");
    }

    private void OnOrderBookUpdate(object? sender, OrderBook book)
    {
        var currentBidQuote = _bidQuoter.LatestQuote;
        var currentAskQuote = _askQuoter.LatestQuote;

        var currentQuotePair = (currentBidQuote.HasValue || currentAskQuote.HasValue)
            ? new QuotePair(_instrument.InstrumentId, currentBidQuote ?? default, currentAskQuote ?? default, 0)
            : (QuotePair?)null;

        // Only print if the quotes have changed since the last print.
        if (currentQuotePair.Equals(_lastDisplayedQuotePair))
        {
            return;
        }

        _lastDisplayedQuotePair = currentQuotePair;
        PrintOrderBookWithQuotes(book, currentBidQuote, currentAskQuote);
    }

    private void PrintOrderBookWithQuotes(OrderBook book, Quote? myBid, Quote? myAsk)
    {
        const int levelsToShow = 10;
        const int myQuoteWidth = 12;
        const int priceWidth = 14;
        const int sizeWidth = 14;

        var sb = new StringBuilder();

        sb.AppendLine("".PadRight(80, '-'));
        sb.AppendLine($"| {DateTime.UtcNow:HH:mm:ss.fff} | QUOTING DEBUGGER FOR {book.Symbol.ToUpper()}");
        sb.AppendLine("".PadRight(80, '-'));

        // Header
        sb.Append($"| {"MY ASK".PadLeft(myQuoteWidth)} ");
        sb.Append($"| {"ASK SIZE".PadRight(sizeWidth)} | {"ASK PRICE".PadLeft(priceWidth)} ");
        sb.Append($"| {"BID PRICE".PadLeft(priceWidth)} | {"BID SIZE".PadRight(sizeWidth)} ");
        sb.AppendLine($"| {"MY BID".PadRight(myQuoteWidth)} |");
        sb.AppendLine("".PadRight(80, '-'));

        var asks = book.Asks.Take(levelsToShow).ToList();
        var bids = book.Bids.Take(levelsToShow).ToList();

        // Main book levels
        for (int i = 0; i < levelsToShow; i++)
        {
            var askLevel = i < asks.Count ? asks[i] : null;
            var bidLevel = i < bids.Count ? bids[i] : null;

            // --- Column 1: My Ask Quantity ---
            string myAskStr = "";
            if (myAsk.HasValue && askLevel != null && myAsk.Value.Price == askLevel.Price)
            {
                myAskStr = myAsk.Value.Size.ToDecimal().ToString("F4");
            }
            sb.Append($"| {myAskStr.PadLeft(myQuoteWidth)} ");

            // --- Column 2 & 3: Market Ask Price & Size ---
            string askPriceStr = askLevel?.Price.ToDecimal().ToString("F4") ?? "";
            string askSizeStr = askLevel?.TotalQuantity.ToDecimal().ToString("F4") ?? "";
            sb.Append($"| {askSizeStr.PadRight(sizeWidth)} | {askPriceStr.PadLeft(priceWidth)} ");

            // --- Column 4 & 5: Market Bid Price & Size ---
            string bidPriceStr = bidLevel?.Price.ToDecimal().ToString("F4") ?? "";
            string bidSizeStr = bidLevel?.TotalQuantity.ToDecimal().ToString("F4") ?? "";
            sb.Append($"| {bidPriceStr.PadLeft(priceWidth)} | {bidSizeStr.PadRight(sizeWidth)} ");

            // --- Column 6: My Bid Quantity ---
            string myBidStr = "";
            if (myBid.HasValue && bidLevel != null && myBid.Value.Price == bidLevel.Price)
            {
                myBidStr = myBid.Value.Size.ToDecimal().ToString("F4");
            }
            sb.AppendLine($"| {myBidStr.PadRight(myQuoteWidth)} |");
        }

        sb.AppendLine("".PadRight(80, '-'));

        // --- Off-Book Quotes Section ---
        bool offBookHeaderPrinted = false;

        // Check if my ask is off-book
        if (myAsk.HasValue && !asks.Any(a => a.Price == myAsk.Value.Price))
        {
            if (!offBookHeaderPrinted)
            {
                sb.AppendLine("| OFF-BOOK QUOTES (not in top 5 levels)");
                offBookHeaderPrinted = true;
            }
            sb.AppendLine($"|   MY ASK -> Price: {myAsk.Value.Price.ToDecimal():F4}, Size: {myAsk.Value.Size.ToDecimal():F4}");
        }

        // Check if my bid is off-book
        if (myBid.HasValue && !bids.Any(b => b.Price == myBid.Value.Price))
        {
            if (!offBookHeaderPrinted)
            {
                sb.AppendLine("| OFF-BOOK QUOTES (not in top 5 levels)");
                offBookHeaderPrinted = true;
            }
            sb.AppendLine($"|   MY BID -> Price: {myBid.Value.Price.ToDecimal():F4}, Size: {myBid.Value.Size.ToDecimal():F4}");
        }

        if (offBookHeaderPrinted)
        {
            sb.AppendLine("".PadRight(80, '-'));
        }

        Console.Clear();
        _logger.LogInformationWithCaller(sb.ToString());
    }
}