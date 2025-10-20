
// See https://aka.ms/new-console-template for more information

// --- 1. logger ---
using System.Collections.Concurrent;
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
using OpenHFT.Processing;
using OpenHFT.Quoting;
using OpenHFT.Quoting.FairValue;
using OpenHFT.Quoting.Validators;
using Serilog;

// --- 1. Logger
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

// --- 2. Config
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    staticLogger.LogInformationWithCaller("Cancellation request received. Shutting down...");
    e.Cancel = true; // 기본 종료 동작 방지
    cts.Cancel();
};

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

// --- 3. object creation ---
var instrumentRepository = new InstrumentRepository(loggerFactory.CreateLogger<InstrumentRepository>());
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
var targetInstrument = instrumentRepository.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX);
if (targetInstrument == null)
{
    staticLogger.LogWarningWithCaller($"Can not find symbol");
    return;
}

var adapters = new ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, BaseFeedAdapter>>();
var disruptor = new Disruptor<MarketDataEventWrapper>(() => new MarketDataEventWrapper(), 1024, TaskScheduler.Default, ProducerType.Multi, new BlockingWaitStrategy());
var feedHandler = new FeedHandler(
    loggerFactory.CreateLogger<FeedHandler>(),
    adapters,
    disruptor
);
CreateAndAddAdaptersFromConfig(config, adapters, instrumentRepository, loggerFactory);

var distributor = new MarketDataDistributor(
    disruptor,
    loggerFactory.CreateLogger<MarketDataDistributor>()
);

var marketDataManager = new MarketDataManager(loggerFactory.CreateLogger<MarketDataManager>(), distributor, instrumentRepository, config);
var subscriptionManager = new SubscriptionManager(loggerFactory.CreateLogger<SubscriptionManager>(), feedHandler, instrumentRepository, config);
await feedHandler.StartAsync(cts.Token);
await distributor.StartAsync(cts.Token);
var bidQuoter = new LogQuoter();
var askQuoter = new LogQuoter();
var validator = new DefaultQuoteValidator(loggerFactory.CreateLogger<DefaultQuoteValidator>(), targetInstrument, marketDataManager);
var mm = new MarketMaker(loggerFactory.CreateLogger<MarketMaker>(), targetInstrument, bidQuoter, askQuoter, validator);
var fvFactory = new FairValueProviderFactory(loggerFactory.CreateLogger<FairValueProviderFactory>());
var engine = new QuotingEngine(loggerFactory.CreateLogger<QuotingEngine>(), targetInstrument, marketDataManager, mm, fvFactory);
var param = new QuotingParameters(targetInstrument.InstrumentId, FairValueModel.Midp, 2, 1, Quantity.FromDecimal(1m), 1);
engine.UpdateParameters(param);
engine.Start();
staticLogger.LogInformationWithCaller("QuotLogger starting...");
var debugger = new QuotingDebugger(targetInstrument, marketDataManager, engine, bidQuoter, askQuoter, loggerFactory.CreateLogger<QuotingDebugger>());
debugger.Start();
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
await feedHandler.StopAsync(CancellationToken.None); // 종료 시에는 새 토큰 사용
await distributor.StopAsync(CancellationToken.None);
staticLogger.LogInformation("Feed Monitor stopped.");

void CreateAndAddAdaptersFromConfig(SubscriptionConfig config,
                                    ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, BaseFeedAdapter>> adapters,
                                    IInstrumentRepository instrumentRepository,
                                    ILoggerFactory loggerFactory)
{
    foreach (var group in config.Subscriptions)
    {
        if (!Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange))
        {
            staticLogger.LogWarningWithCaller($"Unknown exchange '{group.Exchange}' in config.json. Skipping.");
            continue;
        }

        if (!Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
        {
            staticLogger.LogWarningWithCaller($"Unknown product-type '{group.ProductType}' for exchange '{group.Exchange}' in config.json. Skipping.");
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
                staticLogger.LogWarningWithCaller($"No adapter implementation for exchange '{exchange}'. Skipping.");
                break;
        }

        if (newAdapter != null)
        {
            feedHandler.AddAdapter(newAdapter);
            staticLogger.LogInformationWithCaller($"Created and added adapter for {exchange}/{productType}.");
        }
    }
}
