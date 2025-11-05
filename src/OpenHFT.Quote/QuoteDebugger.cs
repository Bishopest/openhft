using System.Text;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
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

        var engine = instance.Engine;
        _instrumentToMonitor = engine.QuotingInstrument;
        engine.QuotePairCalculated += OnQuotePairCalculated;
        _marketDataManager.SubscribeOrderBook(_instrumentToMonitor.InstrumentId, "QuoteDebugger_{_instrumentToMonitor.Symbol}", OnOrderBookUpdate);

        _logger.LogInformationWithCaller($"Quote Debugger started. Monitoring {_instrumentToMonitor.Symbol}");

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

        var allLevels = new SortedDictionary<Price, (Quantity? BidSize, Quantity? AskSize)>(
            Comparer<Price>.Create((x, y) => y.CompareTo(x)) // Price 내림차순 정렬
        );

        // 시장 호가 추가
        foreach (var level in book.Bids)
        {
            allLevels[level.Price] = (level.TotalQuantity, null);
        }
        foreach (var level in book.Asks)
        {
            allLevels[level.Price] = (null, level.TotalQuantity);
        }

        // 나의 호가 추가 (병합)
        if (myBid.HasValue)
        {
            if (allLevels.TryGetValue(myBid.Value.Price, out var sizes))
                allLevels[myBid.Value.Price] = (sizes.BidSize, sizes.AskSize); // 가격만 표시하기 위해 사이즈는 null로 둠
            else
                allLevels[myBid.Value.Price] = (null, null);
        }
        if (myAsk.HasValue)
        {
            if (allLevels.TryGetValue(myAsk.Value.Price, out var sizes))
                allLevels[myAsk.Value.Price] = (sizes.BidSize, sizes.AskSize);
            else
                allLevels[myAsk.Value.Price] = (null, null);
        }

        // --- 2. 표시할 레벨 필터링 ---
        var bestBid = book.GetBestBid().price;
        var bestAsk = book.GetBestAsk().price;

        var relevantLevels = allLevels
            .Where(kvp =>
                (bestBid.ToTicks() > 0 && kvp.Key <= bestBid) || // Bid 사이드
                (bestAsk.ToTicks() > 0 && kvp.Key >= bestAsk) || // Ask 사이드
                (myBid.HasValue && kvp.Key == myBid.Value.Price) || // My Bid
                (myAsk.HasValue && kvp.Key == myAsk.Value.Price)    // My Ask
            )
            .OrderByDescending(kvp => kvp.Key)
            .ToList();

        // 스프레드 주변의 상위 N개만 표시
        var bestAskIndex = relevantLevels.FindIndex(kvp => kvp.Key == bestAsk);
        if (bestAskIndex == -1 && relevantLevels.Any()) bestAskIndex = relevantLevels.Count - 1;

        var startIndex = Math.Max(0, bestAskIndex - levelsToShow + 1);
        var endIndex = Math.Min(relevantLevels.Count - 1, bestAskIndex + levelsToShow);

        var displayLevels = relevantLevels.GetRange(startIndex, endIndex - startIndex + 1);

        // --- 3. 문자열 빌드 ---
        var sb = new StringBuilder();
        const int sizeWidth = 14;
        const int priceWidth = 14;
        const int myQuoteWidth = 14;

        sb.AppendLine("".PadRight(80, '-'));
        sb.AppendLine($"| {DateTime.UtcNow:HH:mm:ss.fff} | INTEGRATED ORDER BOOK FOR {book.Symbol.ToUpper()}");
        sb.AppendLine("".PadRight(80, '-'));

        // Header
        sb.Append($"| {"MY ASK".PadLeft(myQuoteWidth)} ");
        sb.Append($"| {"ASK SIZE".PadLeft(sizeWidth)} ");
        sb.Append($"| {"PRICE".PadLeft(priceWidth)} ");
        sb.Append($"| {"BID SIZE".PadRight(sizeWidth)} ");
        sb.AppendLine($"| {"MY BID".PadRight(myQuoteWidth)} |");
        sb.AppendLine("".PadRight(80, '-'));

        // Body
        foreach (var (price, (bidSize, askSize)) in displayLevels)
        {
            string myAskStr = (myAsk.HasValue && myAsk.Value.Price == price)
                ? myAsk.Value.Size.ToDecimal().ToString("F4") : "";

            string myBidStr = (myBid.HasValue && myBid.Value.Price == price)
                ? myBid.Value.Size.ToDecimal().ToString("F4") : "";

            string askSizeStr = askSize?.ToDecimal().ToString("F4") ?? "";
            string bidSizeStr = bidSize?.ToDecimal().ToString("F4") ?? "";
            string priceStr = price.ToDecimal().ToString("F4");

            // 스프레드 강조
            bool isBestAsk = price == bestAsk;
            bool isBestBid = price == bestBid;

            sb.Append($"| {myAskStr.PadLeft(myQuoteWidth)} ");
            sb.Append($"| {askSizeStr.PadLeft(sizeWidth)} ");

            if (isBestAsk) sb.Append("|>"); else sb.Append("| ");
            sb.Append($"{priceStr.PadLeft(priceWidth - 2)} ");
            if (isBestBid) sb.Append("<|"); else sb.Append(" |");

            sb.Append($" {bidSizeStr.PadRight(sizeWidth)} ");
            sb.AppendLine($"| {myBidStr.PadRight(myQuoteWidth)} |");
        }

        sb.AppendLine("".PadRight(80, '-'));

        sb.AppendLine("| MY TARGET QUOTES");

        string bidSummary = myBid.HasValue
            ? $"BID: {myBid.Value.Price.ToDecimal(),12:F4} @ {myBid.Value.Size.ToDecimal(),-12:F4}"
            : "BID: HELD / CANCELLED";
        string askSummary = myAsk.HasValue
            ? $"ASK: {myAsk.Value.Price.ToDecimal(),12:F4} @ {myAsk.Value.Size.ToDecimal(),-12:F4}"
            : "ASK: HELD / CANCELLED";

        sb.AppendLine($"|   {askSummary}");
        sb.AppendLine($"|   {bidSummary}");
        sb.AppendLine("".PadRight(80, '-'));

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
