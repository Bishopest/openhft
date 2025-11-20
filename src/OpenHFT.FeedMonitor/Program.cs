using System.Collections.Concurrent;
using System.Reflection;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenHFT.Core.Orders;
using Microsoft.Extensions.Configuration;
using OpenHFT.FeedMonitor.Hosting;
using Microsoft.Extensions.Options;
using OpenHFT.Gateway;
using OpenHFT.Gateway.Interfaces;
using OpenHFT.Service;

public class Program
{
    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/feed_monitor-.log", rollingInterval: RollingInterval.Day)
            .CreateBootstrapLogger();

        try
        {
            await CreateHostBuilder(args).Build().RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog((context, services, configuration) => configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File("logs/feed_monitor-.log", rollingInterval: RollingInterval.Day)
            )
            .ConfigureAppConfiguration((hostingContext, configBuilder) =>
            {
                configBuilder.AddJsonFile("config.json", optional: false, reloadOnChange: false);
            })
            .ConfigureServices((hostContext, services) =>
            {
                // --- 1. 설정 객체 등록 ---
                // "subscriptions" 키가 SubscriptionConfig 클래스의 속성과 일치해야 합니다.
                // config.json에 { "Subscriptions": [...] } 구조라면 .GetSection("Subscriptions") 사용
                services.Configure<SubscriptionConfig>(hostContext.Configuration);
                services.AddSingleton(provider => provider.GetRequiredService<IOptions<SubscriptionConfig>>().Value);
                services.AddHttpClient();

                // --- 2. Disruptor  ---
                services.AddSingleton<MarketDataDistributor>();
                services.AddSingleton<IOrderUpdateHandler, OrderUpdateDistributor>();
                services.AddSingleton(provider =>
                {
                    var disruptor = new Disruptor<MarketDataEventWrapper>(() => new MarketDataEventWrapper(), 1024);
                    disruptor.HandleEventsWith(provider.GetRequiredService<MarketDataDistributor>());
                    return disruptor;
                });
                services.AddSingleton(provider =>
                {
                    var disruptor = new Disruptor<OrderStatusReportWrapper>(() => new OrderStatusReportWrapper(), 1024);
                    disruptor.HandleEventsWith(provider.GetRequiredService<IOrderUpdateHandler>());
                    return disruptor;
                });

                // --- 3. 핵심 컴포넌트 등록 ---
                services.AddSingleton<IInstrumentRepository, InstrumentRepository>();
                services.AddSingleton<IOrderRouter, OrderRouter>();
                services.AddSingleton<IRestApiClientRegistry, RestApiClientRegistry>();
                services.AddSingleton<IFeedAdapterRegistry, FeedAdapterRegistry>();
                services.AddSingleton<IFeedHandler, FeedHandler>();
                services.AddSingleton<ISubscriptionManager, SubscriptionManager>();
                services.AddSingleton<MarketDataManager>();
                services.AddSingleton<FeedMonitor>();
                services.AddSingleton<ITimeSyncManager, TimeSyncManager>();
                services.AddSingleton<IRestApiClientRegistry, RestApiClientRegistry>();

                var subscriptionGroups = hostContext.Configuration.GetSection("subscriptions").Get<List<SubscriptionGroup>>() ?? new();
                foreach (var group in subscriptionGroups.DistinctBy(g => new { g.Exchange, g.ProductType }))
                {
                    if (Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange) &&
                        Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
                    {

                        var executionConfig = group.Execution;
                        switch (exchange)
                        {
                            case ExchangeEnum.BINANCE:
                                services.AddSingleton<BaseRestApiClient>(provider => new BinanceRestApiClient(
                                    provider.GetRequiredService<ILogger<BinanceRestApiClient>>(),
                                    provider.GetRequiredService<IInstrumentRepository>(),
                                    provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BinanceRestApiClient)),
                                    productType, executionConfig.Api
                                    ));
                                services.AddSingleton<IFeedAdapter>(provider => new BinanceAdapter(
                                    provider.GetRequiredService<ILogger<BinanceAdapter>>(), productType,
                                    provider.GetRequiredService<IInstrumentRepository>(), executionConfig.Feed
                                ));
                                break;
                            case ExchangeEnum.BITMEX:
                                services.AddSingleton<BaseRestApiClient>(provider => new BitmexRestApiClient(
                                    provider.GetRequiredService<ILogger<BitmexRestApiClient>>(),
                                    provider.GetRequiredService<IInstrumentRepository>(),
                                    provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BitmexRestApiClient)),
                                    productType,
                                    executionConfig.Api));
                                services.AddSingleton<IFeedAdapter>(provider => new BitmexAdapter(
                                    provider.GetRequiredService<ILogger<BitmexAdapter>>(), productType,
                                    provider.GetRequiredService<IInstrumentRepository>(), executionConfig.Feed
                                ));
                                break;
                        }
                    }
                }

                // --- 4. IHostedService 등록 ---
                services.AddHostedService<DisruptorService>();
                services.AddHostedService<FeedOrchestrator>();
                services.AddHostedService<StatisticsService>();
                services.AddHostedService<SubscriptionInitializationService>();
            });
}
