using System.Collections.Concurrent;
using System.Reflection;
using System.Net.Http;
using System.Text;
using Disruptor;
using Disruptor.Dsl;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;
using OpenHFT.Processing;
using Serilog;
using OpenHFT.Gateway.ApiClient;
using OpenHFT.Book.Core;

// --- 1. logger ---
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/feed_monitor-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var loggerFactory = new LoggerFactory().AddSerilog(Log.Logger);
var staticLogger = loggerFactory.CreateLogger<Program>();
TimeSync.Initialize(loggerFactory.CreateLogger(nameof(TimeSync)));

// --- 2. object creation ---
var instrumentRepository = new InstrumentRepository(loggerFactory.CreateLogger<InstrumentRepository>());
var adapters = new ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, BaseFeedAdapter>>();

var disruptor = new Disruptor<MarketDataEventWrapper>(() => new MarketDataEventWrapper(), 1024, TaskScheduler.Default, ProducerType.Multi, new BlockingWaitStrategy());
var feedHandler = new FeedHandler(
    loggerFactory.CreateLogger<FeedHandler>(),
    adapters,
    disruptor
);

var distributor = new MarketDataDistributor(
    disruptor,
    loggerFactory.CreateLogger<MarketDataDistributor>()
);

// CancellationTokenSource
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    staticLogger.LogInformationWithCaller("Cancellation request received. Shutting down...");
    e.Cancel = true; // 기본 종료 동작 방지
    cts.Cancel();
};

staticLogger.LogInformationWithCaller("Feed Monitor starting...");

// ConfigLoader를 사용하여 설정 파일 로드
ConfigLoader configLoader;
SubscriptionConfig config;
string dataFolder;
try
{
    configLoader = new ConfigLoader(loggerFactory.CreateLogger<ConfigLoader>(), "config.json");
    config = configLoader.Deserialize<SubscriptionConfig>();
    dataFolder = configLoader.Get("data-folder");
}
catch (Exception ex)
{
    staticLogger.LogError(ex, "Failed to load or parse configuration.");
    return;
}

var feedMonitor = new FeedMonitor(feedHandler, distributor, loggerFactory.CreateLogger<FeedMonitor>(), config, instrumentRepository);
Timer? statisticsTimer = null;
Timer? binanceTimeSyncTimer = null;
Timer? bitmexTimeSyncTimer = null;

// MarketDataDistributor 시작
await distributor.StartAsync(cts.Token);

CreateAndAddAdaptersFromConfig(config, adapters, instrumentRepository, loggerFactory);

var instrumentsCsvPath = Path.Combine(dataFolder, "instruments.csv");
try
{
    instrumentRepository.LoadFromCsv(instrumentsCsvPath);
}
catch (FileNotFoundException ex)
{
    staticLogger.LogError(ex, "Could not find instruments file at '{Path}'.", instrumentsCsvPath);
    return;
}
// FeedMonitor를 시작하여 FeedHandler 이벤트를 구독
await feedMonitor.StartAsync(cts.Token);
feedMonitor.OnAlert += OnFeedAlert;

// FeedHandler를 시작하여 모든 어댑터 연결 시작
await feedHandler.StartAsync(cts.Token);

//await SubscribeToInstrumentsFromConfig(config, feedHandler, instrumentRepository, cts.Token);
var marketDataManager = new MarketDataManager(loggerFactory.CreateLogger<MarketDataManager>(), distributor, instrumentRepository, config);
var subscriptionManager = new SubscriptionManager(loggerFactory.CreateLogger<SubscriptionManager>(), feedHandler, instrumentRepository, config);

// SubscribeToMidPriceLogging(marketDataManager, config, instrumentRepository);
// SubscribeToMidPriceDiffLogging(marketDataManager, config, instrumentRepository);

await SyncTimeWithBinance();
await SyncTimeWithBitmex();
// await subscriptionManager.InitializeSubscriptionsAsync(); // 이제 자동으로 처리됩니다.

// Start timers
// Periodically sync time with Binance server to adjust for clock drift.
binanceTimeSyncTimer = new Timer(_ => SyncTimeWithBinance().Wait(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
bitmexTimeSyncTimer = new Timer(_ => SyncTimeWithBitmex().Wait(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
// Print statistics every 5 seconds.
statisticsTimer = new Timer(PrintStatistics, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

staticLogger.LogInformation("Feed Monitor started successfully. Press Ctrl+C to exit.");

// 애플리케이션이 종료될 때까지 대기
try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (TaskCanceledException)
{
    // 정상적인 종료
}

// --- 4. 애플리케이션 종료 처리 ---
staticLogger.LogInformation("Feed Monitor stopping...");
statisticsTimer?.Change(Timeout.Infinite, 0);
binanceTimeSyncTimer?.Change(Timeout.Infinite, 0);
bitmexTimeSyncTimer?.Change(Timeout.Infinite, 0);
feedMonitor.OnAlert -= OnFeedAlert;
await feedHandler.StopAsync(CancellationToken.None); // 종료 시에는 새 토큰 사용
await distributor.StopAsync(CancellationToken.None);
staticLogger.LogInformation("Feed Monitor stopped.");


// --- 로컬 함수들 ---
async Task SyncTimeWithBinance()
{
    staticLogger.LogInformationWithCaller("Attempting to synchronize time with Binance server...");
    try
    {
        // We only need one type of client to get the time. PerpetualFuture is a safe bet.
        using var httpClient = new HttpClient();
        var apiClient = new BinanceRestApiClient(loggerFactory.CreateLogger<BinanceRestApiClient>(), instrumentRepository, httpClient, ProductType.PerpetualFuture);
        var serverTimeResponse = await apiClient.GetServerTimeAsync(cts.Token);

        TimeSync.UpdateTimeOffset(ExchangeEnum.BINANCE, serverTimeResponse.ServerTime);
    }
    catch (Exception ex)
    {
        staticLogger.LogError(ex, "Failed to synchronize time with Binance server. Latency calculations may be inaccurate.");
    }
}

async Task SyncTimeWithBitmex()
{
    staticLogger.LogInformationWithCaller("Attempting to synchronize time with Bitmex server...");
    try
    {
        // We only need one type of client to get the time. PerpetualFuture is a safe bet.
        using var httpClient = new HttpClient();
        var apiClient = new BitmexRestApiClient(loggerFactory.CreateLogger<BitmexRestApiClient>(), instrumentRepository, httpClient, ProductType.PerpetualFuture);
        var serverTimeResponse = await apiClient.GetServerTimeAsync(cts.Token);

        TimeSync.UpdateTimeOffset(ExchangeEnum.BITMEX, serverTimeResponse);
    }
    catch (Exception ex)
    {
        staticLogger.LogError(ex, "Failed to synchronize time with Binance server. Latency calculations may be inaccurate.");
    }
}


void OnFeedAlert(object? sender, FeedAlert alert)
{
    // FeedMonitor에서 발생한 알림을 로그로 출력
    var logLevel = alert.Level switch
    {
        AlertLevel.Info => LogLevel.Information,
        AlertLevel.Warning => LogLevel.Warning,
        AlertLevel.Error => LogLevel.Error,
        AlertLevel.Critical => LogLevel.Critical,
        _ => LogLevel.Debug
    };
    staticLogger.Log(logLevel, "FEED ALERT [{Source}/{Level}]: {Message}", alert.SourceExchange, alert.Level, alert.Message);
}

void PrintStatistics(object? state)
{
    // 리플렉션을 사용해 FeedMonitor의 비공개 통계 데이터에 접근
    var statisticsField = typeof(FeedMonitor).GetField("_statistics", BindingFlags.NonPublic | BindingFlags.Instance);
    if (statisticsField?.GetValue(feedMonitor) is not ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, ConcurrentDictionary<int, FeedStatistics>>> statsDict)
    {
        return;
    }

    var sb = new StringBuilder();
    BuildStatisticsString(sb, statsDict);

    // 기존 콘솔 내용을 지우고 새로운 통계 출력 (콘솔이 깜빡거리는 효과)
    // Console.Clear();
    // Console.WriteLine(sb.ToString());
    // 로거를 통해 파일에도 기록 (필요 시)
    staticLogger.LogInformationWithCaller($"Feed Statistics Update:\n{sb.ToString()}");
}

void BuildStatisticsString(StringBuilder sb, ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, ConcurrentDictionary<int, FeedStatistics>>> statsDict)
{
    sb.AppendLine("\n--- Feed Statistics ---");
    sb.AppendLine($"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");

    var offsets = TimeSync.GetAllOffsetsMillis();
    if (offsets.Any())
    {
        var offsetStrings = string.Join(", ", offsets.Select(kvp => $"{kvp.Key}: {kvp.Value}ms"));
        sb.AppendLine($"Time Offsets: {offsetStrings}");
    }
    sb.AppendLine("---------------------------------------------------------------------------------------------------------------------------------");
    sb.AppendLine("| Exchange         | Product Type     | Topic            | Msgs/sec | Avg Latency(ms) | P95 Latency(ms) | Gaps | Drop(%)  | Reconnects |");
    sb.AppendLine("---------------------------------------------------------------------------------------------------------------------------------");


    if (statsDict.IsEmpty)
    {
        sb.AppendLine("| No data available yet...                                                                        |");
    }
    else
    {
        foreach (var exchangePair in statsDict.OrderBy(p => p.Key))
        {
            foreach (var productPair in exchangePair.Value.OrderBy(p => p.Key))
            {
                foreach (var topicPair in productPair.Value.OrderBy(p => p.Key))
                {
                    var topicId = topicPair.Key;
                    var stats = topicPair.Value;
                    var topicName = TopicRegistry.TryGetTopic(topicId, out var topic) ? topic.GetTopicName() : $"Unknown({topicId})";

                    sb.Append($"| {exchangePair.Key,-16} | {productPair.Key,-16} | {topicName,-16} ");
                    sb.Append($"| {stats.MessagesPerSecond,-8:F0} ");
                    sb.Append($"| {stats.AvgE2ELatency,-15:F2} ");
                    sb.Append($"| {stats.GetLatencyPercentile(0.95),-15:F2} ");
                    sb.Append($"| {stats.SequenceGaps,-4} ");
                    sb.Append($"| {stats.DropRate,-8:P2} ");
                    sb.Append($"| {stats.ReconnectCount,-10} |");
                    sb.AppendLine();
                }
            }
        }
    }
    sb.AppendLine("---------------------------------------------------------------------------------------------------------------------------------");
}

void SubscribeToMidPriceLogging(MarketDataManager manager, SubscriptionConfig subConfig, IInstrumentRepository repo)
{
    var lastOrderBookMidPrices = new ConcurrentDictionary<int, long>();
    var lastBestOrderBookMidPrices = new ConcurrentDictionary<int, long>();

    void OnOrderBookUpdate(object? sender, OrderBook book)
    {
        var newMidPrice = book.GetMidPriceTicks();
        if (newMidPrice == 0) return;

        var lastMidPrice = lastOrderBookMidPrices.GetOrAdd(book.InstrumentId, newMidPrice);

        if (newMidPrice != lastMidPrice)
        {
            if (lastOrderBookMidPrices.TryUpdate(book.InstrumentId, newMidPrice, lastMidPrice))
            {
                staticLogger.LogInformationWithCaller($"[OrderBook] {book.Symbol} Mid-Price changed: {lastMidPrice} -> {newMidPrice}");
                staticLogger.LogInformationWithCaller($"{book.ToTerminalString()}");
            }
        }
    }

    void OnBestOrderBookUpdate(object? sender, BestOrderBook book)
    {
        var newMidPrice = book.GetMidPriceTicks();
        if (newMidPrice == 0) return;

        var lastMidPrice = lastBestOrderBookMidPrices.GetOrAdd(book.InstrumentId, newMidPrice);

        if (newMidPrice != lastMidPrice)
        {
            if (lastBestOrderBookMidPrices.TryUpdate(book.InstrumentId, newMidPrice, lastMidPrice))
            {
                staticLogger.LogInformationWithCaller($"[BestOrderBook] {book.Symbol} Mid-Price changed: {lastMidPrice} -> {newMidPrice}");
                staticLogger.LogInformationWithCaller($"{book.ToTerminalString()}");
            }
        }
    }

    staticLogger.LogInformation("Setting up mid-price change logging for all configured instruments...");
    foreach (var group in subConfig.Subscriptions)
    {
        if (!Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange))
        {
            staticLogger.LogWarning($"Unknown exchange '{group.Exchange}' in config.json. Skipping.");
            continue;
        }

        if (!Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
        {
            staticLogger.LogWarning($"Unknown product-type '{group.ProductType}' for exchange '{group.Exchange}' in config.json. Skipping.");
            continue;
        }

        foreach (var symbol in group.Symbols)
        {
            var inst = repo.FindBySymbol(symbol, productType, exchange);
            if (inst != null)
            {
                // manager.SubscribeOrderBook(inst, "GlobalMidPriceLogger", OnOrderBookUpdate);
                manager.SubscribeBestOrderBook(inst, "GlobalMidPriceLogger", OnBestOrderBookUpdate);
            }
        }

    }
}

void SubscribeToMidPriceDiffLogging(MarketDataManager manager, SubscriptionConfig subConfig, IInstrumentRepository repo)
{
    var lastBestOrderBookMidPrices = new ConcurrentDictionary<int, long>();

    void OnBestOrderBookUpdate(object? sender, BestOrderBook book)
    {
        var newMidPrice = book.GetMidPriceTicks();
        if (newMidPrice == 0) return;

        var lastMidPrice = lastBestOrderBookMidPrices.GetOrAdd(book.InstrumentId, newMidPrice);

        if (newMidPrice != lastMidPrice)
        {
            if (lastBestOrderBookMidPrices.TryUpdate(book.InstrumentId, newMidPrice, lastMidPrice))
            {

                // staticLogger.LogInformationWithCaller($"[BestOrderBook] {book.Symbol} Mid-Price changed: {lastMidPrice} -> {newMidPrice}");
                var baseMid = lastBestOrderBookMidPrices.Where(kvp => kvp.Key != book.InstrumentId).FirstOrDefault().Value;
                var diff = book.SourceExchange == ExchangeEnum.BINANCE ? (newMidPrice - baseMid) : (baseMid - newMidPrice);
                if (diff > -650000 | diff < -1000000)
                {
                    staticLogger.LogInformationWithCaller($"[BestOrderBook] [Binance - Bitmex] {book.Symbol} Mid-Price diff: {diff}");
                }
                // staticLogger.LogInformationWithCaller($"{book.ToTerminalString()}");

            }
        }
    }

    staticLogger.LogInformation("Setting up mid-price change logging for all configured instruments...");
    foreach (var group in subConfig.Subscriptions)
    {
        if (!Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange))
        {
            staticLogger.LogWarning($"Unknown exchange '{group.Exchange}' in config.json. Skipping.");
            continue;
        }

        if (!Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
        {
            staticLogger.LogWarning($"Unknown product-type '{group.ProductType}' for exchange '{group.Exchange}' in config.json. Skipping.");
            continue;
        }

        foreach (var symbol in group.Symbols)
        {
            var inst = repo.FindBySymbol(symbol, productType, exchange);
            if (inst != null)
            {
                // manager.SubscribeOrderBook(inst, "GlobalMidPriceLogger", OnOrderBookUpdate);
                manager.SubscribeBestOrderBook(inst, "GlobalMidPriceLogger", OnBestOrderBookUpdate);
            }
        }

    }
}


void CreateAndAddAdaptersFromConfig(SubscriptionConfig config,
                                    ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, BaseFeedAdapter>> adapters,
                                    IInstrumentRepository instrumentRepository,
                                    ILoggerFactory loggerFactory)
{
    foreach (var group in config.Subscriptions)
    {
        if (!Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange))
        {
            staticLogger.LogWarning($"Unknown exchange '{group.Exchange}' in config.json. Skipping.");
            continue;
        }

        if (!Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
        {
            staticLogger.LogWarning($"Unknown product-type '{group.ProductType}' for exchange '{group.Exchange}' in config.json. Skipping.");
            continue;
        }

        // 어댑터가 이미 생성되었는지 확인
        if (adapters.TryGetValue(exchange, out var innerDict) && innerDict.ContainsKey(productType))
        {
            continue;
        }

        BaseFeedAdapter? newAdapter = null;
        switch (exchange)
        {
            case ExchangeEnum.BINANCE:
                newAdapter = new BinanceAdapter(loggerFactory.CreateLogger<BinanceAdapter>(), productType, instrumentRepository);
                break;
            // 다른 거래소 어댑터가 있다면 여기에 추가 (e.g., case ExchangeEnum.BYBIT:)
            case ExchangeEnum.BITMEX:
                newAdapter = new BitmexAdapter(loggerFactory.CreateLogger<BitmexAdapter>(), productType, instrumentRepository);
                break;

            default:
                staticLogger.LogWarning($"No adapter implementation for exchange '{exchange}'. Skipping.");
                break;
        }

        if (newAdapter != null)
        {
            feedHandler.AddAdapter(newAdapter);
            staticLogger.LogInformation($"Created and added adapter for {exchange}/{productType}.");
        }
    }
}