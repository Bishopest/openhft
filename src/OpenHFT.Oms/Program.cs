using Disruptor.Dsl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Feed;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Gateway;
using OpenHFT.Gateway.ApiClient;
using OpenHFT.Gateway.Interfaces;
using OpenHFT.Oms.Api.WebSocket;
using OpenHFT.Oms.Api.WebSocket.CommandHandlers;
using OpenHFT.Processing;
using OpenHFT.Quoting;
using OpenHFT.Quoting.FairValue;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Service;
using Serilog;
using DotNetEnv;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenHFT.Core.DataBase;

public class Program
{
    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/oms-.log", rollingInterval: RollingInterval.Day)
            .CreateBootstrapLogger();

        Env.Load();
        Env.TraversePath().Load();
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
                .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File("logs/oms-.log", rollingInterval: RollingInterval.Day)
            )
            .ConfigureAppConfiguration((hostingContext, configBuilder) =>
            {
                configBuilder.AddJsonFile("config.json", optional: false, reloadOnChange: false);
            })
            .ConfigureServices((hostContext, services) =>
            {
                var configuration = hostContext.Configuration;
                services.Configure<SubscriptionConfig>(hostContext.Configuration);
                services.AddSingleton(provider => provider.GetRequiredService<IOptions<SubscriptionConfig>>().Value);
                services.Configure<QuotingConfig>(hostContext.Configuration);
                services.AddSingleton(provider => provider.GetRequiredService<IOptions<QuotingConfig>>().Value);
                services.AddHttpClient();

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                };
                services.AddSingleton(jsonOptions);

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
                services.AddSingleton<IOrderGateway, NullOrderGateway>();
                services.AddSingleton<IRestApiClientRegistry, RestApiClientRegistry>();
                services.AddSingleton<IFeedAdapterRegistry, FeedAdapterRegistry>();
                services.AddSingleton<IFeedHandler, FeedHandler>();
                services.AddSingleton<ISubscriptionManager, SubscriptionManager>();
                services.AddSingleton<MarketDataManager>();
                services.AddSingleton<ITimeSyncManager, NullTimeSyncManager>();
                services.AddSingleton<IRestApiClientRegistry, RestApiClientRegistry>();
                services.AddSingleton<IFairValueProviderFactory, FairValueProviderFactory>();
                services.AddSingleton<IQuoterFactory, QuoterFactory>();
                services.AddSingleton<IQuotingInstanceFactory, QuotingInstanceFactory>();
                services.AddSingleton<IQuotingInstanceManager, QuotingInstanceManager>();
                services.AddSingleton<IOrderGatewayRegistry, OrderGatewayRegistry>();
                services.AddSingleton<IOrderFactory, OrderFactory>();
                services.AddSingleton<QuoteDebugger>();
                services.AddSingleton<WebSocketChannel>();
                services.AddSingleton<IWebSocketChannel>(provider =>
                    provider.GetRequiredService<WebSocketChannel>());
                services.AddSingleton<IWebSocketCommandHandler, UpdateParametersCommandHandler>();
                services.AddSingleton<IWebSocketCommandHandler, RetireInstanceCommandHandler>();
                services.AddSingleton<IWebSocketCommandHandler, GetInstanceStatusesCommandHandler>();
                services.AddSingleton<IWebSocketCommandHandler, GetActiveOrdersCommandHandler>();
                services.AddSingleton<IWebSocketCommandHandler, GetFillsCommandHandler>();
                services.AddSingleton<IWebSocketCommandRouter, WebSocketCommandRouter>();
                services.AddSingleton<IFillRepository, SqliteFillRepository>();

                // --- 3-1. Adapter 및 RestApiClient 등록 ---
                var subscriptionTupLists = new List<(string Exchange, string ProductType)>();
                var subscriptionGroups = hostContext.Configuration.GetSection("subscriptions").Get<List<SubscriptionGroup>>() ?? new();
                foreach (var group in subscriptionGroups.DistinctBy(g => new { g.Exchange, g.ProductType }))
                {
                    if (Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange) &&
                        Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
                    {
                        var executionConfig = group.Execution;
                        var apiKey = configuration[$"{group.Exchange.ToString().ToUpper()}_{group.Execution.Api.ToString().ToUpper()}_API_KEY"];
                        var apiSecret = configuration[$"{group.Exchange.ToString().ToUpper()}_{group.Execution.Api.ToString().ToUpper()}_API_SECRET"];

                        switch (exchange)
                        {
                            case ExchangeEnum.BINANCE:
                                services.AddSingleton<BaseRestApiClient, BinanceRestApiClient>(provider => new BinanceRestApiClient(
                                    provider.GetRequiredService<ILogger<BinanceRestApiClient>>(),
                                    provider.GetRequiredService<IInstrumentRepository>(),
                                    provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BinanceRestApiClient)),
                                    productType,
                                    executionConfig.Api));
                                services.AddSingleton<IFeedAdapter>(provider => new BinanceAdapter(
                                    provider.GetRequiredService<ILogger<BinanceAdapter>>(), productType,
                                    provider.GetRequiredService<IInstrumentRepository>(),
                                    executionConfig.Feed
                                ));
                                break;
                            case ExchangeEnum.BITMEX:
                                services.AddSingleton<BitmexRestApiClient>(provider => new BitmexRestApiClient(
                                    provider.GetRequiredService<ILogger<BitmexRestApiClient>>(),
                                    provider.GetRequiredService<IInstrumentRepository>(),
                                    provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BitmexRestApiClient)),
                                    productType,
                                    executionConfig.Api, apiSecret, apiKey));
                                services.AddSingleton<IOrderGateway, BitmexOrderGateway>(provider => new BitmexOrderGateway(
                                    provider.GetRequiredService<ILogger<BitmexOrderGateway>>(),
                                    provider.GetRequiredService<BitmexRestApiClient>(),
                                    provider.GetRequiredService<IInstrumentRepository>(),
                                    productType));
                                services.AddSingleton<IFeedAdapter>(provider => new BitmexAdapter(
                                    provider.GetRequiredService<ILogger<BitmexAdapter>>(), productType,
                                    provider.GetRequiredService<IInstrumentRepository>(),
                                    executionConfig.Feed
                                ));
                                break;
                        }
                    }
                }

                // --- 4. IHostedService 등록 ---
                services.AddHostedService<DisruptorService>();
                services.AddHostedService<FeedOrchestrator>();
                services.AddHostedService<WebSocketHost>();
                services.AddHostedService<WebSocketNotificationService>();
                services.AddHostedService<FillPersistenceService>();
            }
        );
}