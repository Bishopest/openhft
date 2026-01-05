using System.Collections.Concurrent;
using Disruptor.Dsl;
using DotNetEnv;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Feed;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;
using OpenHFT.Gateway;
using OpenHFT.Gateway.ApiClient;
using OpenHFT.Processing;

namespace OpenHFT.Tests.Core.Models;

[TestFixture, Category("E2E_Integration")]
public class OrderBookIntegrity_E2E_Tests
{
    private ServiceProvider _serviceProvider = null!;
    private IInstrumentRepository _instrumentRepo = null!;
    private IFeedAdapterRegistry _adapterRegistry = null!;
    private IMarketDataManager _marketDataManager = null!;
    private string _testDirectory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Load API keys for live connection if needed (e.g., for private topics)
        Env.Load();
        Env.TraversePath().Load();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

        // --- Instrument Repository Setup ---
        _testDirectory = Path.Combine(Path.GetTempPath(), "E2E_OrderBookTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        var csvContent = @"instrument_id,market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
1,BITMEX,XBTUSD,PerpetualFuture,XBT,USD,0.5,1,1,1
2,BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.01,0.001,1,0.001";
        File.WriteAllText(Path.Combine(_testDirectory, "instruments.csv"), csvContent);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { { "dataFolder", _testDirectory } })
            .Build();

        services.AddSingleton(configuration);
        services.AddSingleton<IInstrumentRepository, InstrumentRepository>();

        // --- Core Services ---
        services.AddSingleton(new SubscriptionConfig());
        services.AddHttpClient();
        services.AddSingleton<MarketDataDistributor>();
        services.AddSingleton(provider =>
        {
            var disruptor = new Disruptor<MarketDataEventWrapper>(() => new MarketDataEventWrapper(), 65536);
            disruptor.HandleEventsWith(provider.GetRequiredService<MarketDataDistributor>());
            return disruptor;
        });
        services.AddSingleton(provider =>
        {
            var disruptor = new Disruptor<OrderStatusReportWrapper>(() => new OrderStatusReportWrapper(), 1024);
            disruptor.HandleEventsWith(provider.GetRequiredService<IOrderUpdateHandler>());
            return disruptor;
        });
        services.AddSingleton<IFeedHandler, FeedHandler>();
        services.AddSingleton<IOrderUpdateHandler, OrderUpdateDistributor>();
        services.AddSingleton<IOrderRouter, OrderRouter>();
        services.AddSingleton<IOrderGateway, NullOrderGateway>();
        // --- Real Adapters and API Clients ---
        // Registering real implementations for E2E test
        services.AddSingleton<BinanceRestApiClient>(provider => new BinanceRestApiClient(
            provider.GetRequiredService<ILogger<BinanceRestApiClient>>(),
            provider.GetRequiredService<IInstrumentRepository>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(),
            ProductType.PerpetualFuture,
            ExecutionMode.Live
        ));

        services.AddSingleton<IFeedAdapter>(provider => new BinanceAdapter(
            provider.GetRequiredService<ILogger<BinanceAdapter>>(),
            ProductType.PerpetualFuture,
            provider.GetRequiredService<IInstrumentRepository>(),
            ExecutionMode.Live,
            provider.GetRequiredService<BinanceRestApiClient>()
        ));

        services.AddSingleton<IFeedAdapter>(provider => new BitmexAdapter(
            provider.GetRequiredService<ILogger<BitmexAdapter>>(),
            ProductType.PerpetualFuture,
            provider.GetRequiredService<IInstrumentRepository>(),
            ExecutionMode.Live
        ));

        services.AddSingleton<IFeedAdapterRegistry, FeedAdapterRegistry>();

        // Use the real MarketDataManager which will create consumers
        services.AddSingleton<IMarketDataManager, MarketDataManager>();

        _serviceProvider = services.BuildServiceProvider();

        // Start necessary services
        _ = _serviceProvider.GetRequiredService<IFeedHandler>();
        _instrumentRepo = _serviceProvider.GetRequiredService<IInstrumentRepository>();
        _adapterRegistry = _serviceProvider.GetRequiredService<IFeedAdapterRegistry>();
        _marketDataManager = _serviceProvider.GetRequiredService<IMarketDataManager>();

        var disruptor = _serviceProvider.GetRequiredService<Disruptor<MarketDataEventWrapper>>();
        var orderDisruptor = _serviceProvider.GetRequiredService<Disruptor<OrderStatusReportWrapper>>();
        disruptor.Start();
        orderDisruptor.Start();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        // Gracefully disconnect all adapters
        foreach (var adapter in _adapterRegistry.GetAllAdapters())
        {
            await adapter.DisconnectAsync();
        }
        _serviceProvider?.Dispose();
        Directory.Delete(_testDirectory, true);
    }

    [TestCase(ExchangeEnum.BINANCE, ProductType.PerpetualFuture, "BTCUSDT", TestName = "Binance_BTCUSDT_Perpetual_OrderBookIntegrity")]
    [TestCase(ExchangeEnum.BITMEX, ProductType.PerpetualFuture, "XBTUSD", TestName = "Bitmex_XBTUSD_Perpetual_OrderBookIntegrity")]
    public async Task LiveStream_ShouldMaintainOrderBookIntegrity_ForDuration(ExchangeEnum exchange, ProductType productType, string symbol)
    {
        // --- Arrange ---
        var instrument = _instrumentRepo.FindBySymbol(symbol, productType, exchange);
        instrument.Should().NotBeNull($"Instrument {symbol} should be found.");

        var adapter = _adapterRegistry.GetAdapter(exchange, productType);
        adapter.Should().NotBeNull($"Adapter for {exchange}/{productType} should be found.");

        var testDuration = TimeSpan.FromSeconds(30);
        var cancellationTokenSource = new CancellationTokenSource(testDuration);
        var validationErrors = new ConcurrentQueue<string>(); // Use thread-safe collection
        var firstUpdateReceived = new TaskCompletionSource<bool>();

        // Install the instrument to create the consumers.
        _marketDataManager.Install(instrument);

        // --- MODIFICATION: Use event-driven validation ---
        // 1. Define the callback method for order book updates.
        void OrderBookUpdateCallback(object? sender, OrderBook book)
        {
            // Set the flag on the first update to signal that data is flowing.
            firstUpdateReceived.TrySetResult(true);

            if (!book.ValidateIntegrity())
            {
                var errorMessage = $"Integrity check failed at {DateTime.UtcNow:HH:mm:ss.fff}. Book state: {book}";
                validationErrors.Enqueue(errorMessage);

                // Optional: Cancel the test immediately on first failure.
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    cancellationTokenSource.Cancel();
                }
            }
        }

        // 2. Subscribe to the MarketDataManager.
        var subscriberName = $"E2E_Test_{symbol}";
        _marketDataManager.SubscribeOrderBook(instrument.InstrumentId, subscriberName, OrderBookUpdateCallback);

        // --- Act ---
        // Connect and subscribe the adapter.
        if (!adapter.IsConnected)
        {
            await adapter.ConnectAsync(cancellationTokenSource.Token);
        }

        var topic = exchange == ExchangeEnum.BINANCE
            ? BinanceTopic.DepthUpdate
            : (ExchangeTopic)BitmexTopic.OrderBook10;

        await adapter.SubscribeAsync(new[] { instrument }, new[] { topic }, cancellationTokenSource.Token);

        TestContext.WriteLine($"Starting {testDuration.TotalSeconds}-second live integrity test for {symbol} on {exchange}. Waiting for first data...");

        // Wait for the first order book update to arrive or timeout.
        var firstUpdateTask = await Task.WhenAny(firstUpdateReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (firstUpdateTask != firstUpdateReceived.Task)
        {
            Assert.Fail("Did not receive any order book data within 10 seconds.");
        }

        TestContext.WriteLine("First data received. Monitoring for test duration...");

        // Now, just wait for the test duration to elapse or for an error to cancel the test.
        try
        {
            await Task.Delay(testDuration, cancellationTokenSource.Token);
        }
        catch (TaskCanceledException)
        {
            // This is expected, either by timeout or by validation failure.
        }

        // --- Assert ---
        TestContext.WriteLine($"Test for {symbol} finished. Found {validationErrors.Count} errors.");

        // Unsubscribe to clean up
        _marketDataManager.UnsubscribeOrderBook(instrument.InstrumentId, subscriberName);

        validationErrors.Should().BeEmpty("The order book should maintain its integrity throughout the test.");
    }
}