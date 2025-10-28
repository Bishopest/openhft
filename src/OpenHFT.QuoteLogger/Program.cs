
// See https://aka.ms/new-console-template for more information

// --- 1. logger ---
using System.Collections.Concurrent;
using Disruptor;
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
using OpenHFT.Core.Utils;
using OpenHFT.Feed;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Gateway;
using OpenHFT.Gateway.ApiClient;
using OpenHFT.Gateway.Interfaces;
using OpenHFT.Processing;
using OpenHFT.Quoting;
using OpenHFT.Quoting.FairValue;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Validators;
using OpenHFT.Service;
using Serilog;

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
                .WriteTo.File("logs/quote_logger-.log", rollingInterval: RollingInterval.Day)
            )
            .ConfigureAppConfiguration((hostingContext, configBuilder) =>
            {
                configBuilder.AddJsonFile("config.json", optional: false, reloadOnChange: false);
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.Configure<QuotingConfig>(hostContext.Configuration);
                services.AddSingleton(provider => provider.GetRequiredService<IOptions<QuotingConfig>>().Value);
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
                // --- 3-1. Adapter 및 RestApiClient 등록 ---
                var subscriptionTupLists = new List<(string Exchange, string ProductType)>();
                var quotingConfig = hostContext.Configuration.GetSection("quoting").Get<QuotingConfig>();
                if (quotingConfig == null)
                {
                    throw new InvalidOperationException("Quoting configuration is missing or invalid in config.json.");
                }
                services.AddSingleton<QuotingConfig>(quotingConfig);
                services.AddSingleton(new QuotingConfig[] { quotingConfig });
                services.AddSingleton(provider =>
                {
                    var qc = provider.GetRequiredService<QuotingConfig>();
                    var subscriptionConfig = new SubscriptionConfig();

                    var mainGroup = new SubscriptionGroup
                    {
                        Exchange = qc.Exchange,
                        ProductType = qc.ProductType,
                        Symbols = new string[] { qc.Symbol }
                    };
                    subscriptionConfig.Subscriptions.Add(mainGroup);

                    var fvGroup = new SubscriptionGroup
                    {
                        Exchange = qc.FairValue.Params.Exchange,
                        ProductType = qc.FairValue.Params.ProductType,
                        Symbols = new string[] { qc.FairValue.Params.Symbol }
                    };

                    var existing = subscriptionConfig.Subscriptions
                        .FirstOrDefault(g => g.Exchange.Equals(fvGroup.Exchange, StringComparison.OrdinalIgnoreCase) &&
                                             g.ProductType.Equals(fvGroup.ProductType, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        if (!existing.Symbols.Contains(fvGroup.Symbols[0]))
                        {
                            existing.Symbols = existing.Symbols.Concat(fvGroup.Symbols).ToArray();
                        }

                    }
                    else
                    {
                        subscriptionConfig.Subscriptions.Add(fvGroup);
                    }
                    return subscriptionConfig;
                });

                var quotingSubscriptionTup = (quotingConfig.Exchange, quotingConfig.ProductType);
                var fvSubscriptionTup = (quotingConfig.FairValue.Params.Exchange, quotingConfig.FairValue.Params.ProductType);
                subscriptionTupLists.Add(quotingSubscriptionTup);
                subscriptionTupLists.Add(fvSubscriptionTup);
                foreach (var tup in subscriptionTupLists)
                {
                    if (Enum.TryParse<ExchangeEnum>(tup.Item1, true, out var exchange) &&
                        Enum.TryParse<ProductType>(tup.Item2, true, out var productType))
                    {
                        switch (exchange)
                        {
                            case ExchangeEnum.BINANCE:
                                services.AddSingleton<BaseRestApiClient>(provider => new BinanceRestApiClient(
                                    provider.GetRequiredService<ILogger<BinanceRestApiClient>>(),
                                    provider.GetRequiredService<IInstrumentRepository>(),
                                    provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BinanceRestApiClient)),
                                    productType));
                                services.AddSingleton<IFeedAdapter>(provider => new BinanceAdapter(
                                    provider.GetRequiredService<ILogger<BinanceAdapter>>(), productType,
                                    provider.GetRequiredService<IInstrumentRepository>()
                                ));
                                break;
                            case ExchangeEnum.BITMEX:
                                services.AddSingleton<BaseRestApiClient>(provider => new BitmexRestApiClient(
                                    provider.GetRequiredService<ILogger<BitmexRestApiClient>>(),
                                    provider.GetRequiredService<IInstrumentRepository>(),
                                    provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BitmexRestApiClient)),
                                    productType));
                                services.AddSingleton<IFeedAdapter>(provider => new BitmexAdapter(
                                    provider.GetRequiredService<ILogger<BitmexAdapter>>(), productType,
                                    provider.GetRequiredService<IInstrumentRepository>()
                                ));
                                break;
                        }
                    }
                }

                // --- 4. IHostedService 등록 ---
                services.AddHostedService<DisruptorService>();
                services.AddHostedService<FeedOrchestrator>();
                services.AddHostedService<SubscriptionInitializationService>();
                services.AddHostedService<QuotingBootstrapService>();
            }
        );
}