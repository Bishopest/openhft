using System.Text;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Utils;
using OpenHFT.Processing;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

/// <summary>
/// An IHostedService that visualizes the output of QuotingInstances.
/// It subscribes to order book updates and monitors the state of mock quoters.
/// </summary>
public class QuoteDebugger
{
    private readonly ILogger<QuoteDebugger> _logger;
    private readonly MarketDataManager _marketDataManager;
    private readonly IQuotingInstanceManager _quotingInstanceManager;

    private Instrument? _instrumentToMonitor;
    private QuotePair? _lastDisplayedQuotePair;

    public QuoteDebugger(
        ILogger<QuoteDebugger> logger,
        MarketDataManager marketDataManager,
        IQuotingInstanceManager quotingInstanceManager
    )
    {
        _logger = logger;
        _marketDataManager = marketDataManager;
        _quotingInstanceManager = quotingInstanceManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Starting QuoteDebugger...");

        var instance = _quotingInstanceManager.GetAllInstances().FirstOrDefault();

        if (instance == null)
        {
            _logger.LogWarningWithCaller("Quoting Debugger: No active QuotingInstance found to monitor.");
            return Task.CompletedTask;
        }

        if (instance.TryGetEngine(out var engine))
        {
            _instrumentToMonitor = engine.QuotingInstrument;
            engine.QuotePairCalculated += OnQuotePairCalculated;
            _marketDataManager.SubscribeOrderBook(_instrumentToMonitor.InstrumentId, "QuoteDebugger_{_instrumentToMonitor.Symbol}", OnOrderBookUpdate);

            _logger.LogInformationWithCaller($"Quote Debugger started. Monitoring {_instrumentToMonitor.Symbol}");
        }

        return Task.CompletedTask;
    }

    private void OnQuotePairCalculated(object? sender, QuotePair pair)
    {
        _lastDisplayedQuotePair = pair;
    }

    private void OnOrderBookUpdate(object? sender, OrderBook ob)
    {
        var currentQuotePair = _lastDisplayedQuotePair;
        var myBid = currentQuotePair?.Bid.Size.ToTicks() > 0 ? currentQuotePair?.Bid : null;
        var myAsk = currentQuotePair?.Ask.Size.ToTicks() > 0 ? currentQuotePair?.Ask : null;

        PrintOrderBookWithQuotes(ob, myBid, myAsk);
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
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_instrumentToMonitor != null)
        {
            _marketDataManager.UnsubscribeOrderBook(_instrumentToMonitor.InstrumentId, $"QuotingDebugger_{_instrumentToMonitor.Symbol}");
            _logger.LogInformationWithCaller($"QuoteDebugger stopped for {_instrumentToMonitor.Symbol}.");
        }

        return Task.CompletedTask;
    }
}
