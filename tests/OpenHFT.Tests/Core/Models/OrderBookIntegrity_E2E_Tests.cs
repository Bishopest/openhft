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
2,BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.01,0.001,1,0.001
3,BITHUMB,KRW-ETH,Spot,ETH,KRW,1000,0.0001,1,0.002";
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

        services.AddSingleton<IFeedAdapter>(provider => new BithumbPublicAdapter(
            provider.GetRequiredService<ILogger<BithumbPublicAdapter>>(),
            ProductType.Spot,
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
    [TestCase(ExchangeEnum.BITHUMB, ProductType.Spot, "KRW-ETH", TestName = "Bithumb_KRW-ETH_Spot_OrderBookIntegrity")]
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

        var topic = GetOrderBookTopic(exchange);

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

    // MarketDataManager에서 orderbook topic을 지정하는 함수 GetTopicForConsumer 가 있으므로
    // orderbook 토픽을 L2로 수정할 때는 GetTopicForConsumer 함수도 수정해줘야함.
    [TestCase(ExchangeEnum.BITMEX, ProductType.PerpetualFuture, "XBTUSD", 3, TestName = "Bitmex_Reconnection_ChaosTest_WithResub")]
    [TestCase(ExchangeEnum.BINANCE, ProductType.PerpetualFuture, "BTCUSDT", 2, TestName = "Binance_Reconnection_ChaosTest_WithResub")]
    [TestCase(ExchangeEnum.BITHUMB, ProductType.Spot, "KRW-ETH", 1, TestName = "Bithumb_Reconnection_ChaosTest_WithResub")]
    public async Task Reconnection_Integrity_ChaosTest_WithResubscription(ExchangeEnum exchange, ProductType productType, string symbol, int chaosIterations)
    {
        // --- 1. Arrange ---
        var instrument = _instrumentRepo.FindBySymbol(symbol, productType, exchange);
        var adapter = _adapterRegistry.GetAdapter(exchange, productType);
        var validationErrors = new ConcurrentQueue<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // 반복마다 데이터 수신을 확인하기 위한 변수
        TaskCompletionSource<bool> iterationDataReceived = new();

        _marketDataManager.Install(instrument);

        void IntegrityCheckCallback(object? sender, OrderBook book)
        {
            // 데이터가 한 번이라도 들어오면 완료 신호를 보냄
            if (book.UpdateCount > 0)
            {
                iterationDataReceived.TrySetResult(true);
            }

            if (!book.ValidateIntegrity())
            {
                var err = $"[Integrity Failure] {exchange} {symbol} at {DateTime.Now:HH:mm:ss.fff}. Book: {book}";
                validationErrors.Enqueue(err);
            }
        }

        var subscriberName = $"ChaosTest_{symbol}";
        _marketDataManager.SubscribeOrderBook(instrument.InstrumentId, subscriberName, IntegrityCheckCallback);

        try
        {
            var topic = GetOrderBookTopic(exchange);

            // --- 2. Act & Chaos Loop ---
            for (int i = 1; i <= chaosIterations; i++)
            {
                // 반복 시작 시 TCS 초기화
                iterationDataReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                TestContext.WriteLine($"\n[Iteration {i}] STEP 1: Connecting and Subscribing...");
                if (!adapter.IsConnected)
                {
                    await adapter.ConnectAsync(cts.Token);
                }
                await adapter.SubscribeAsync(new[] { instrument }, new[] { topic }, cts.Token);

                // [중요] 데이터 수신 확인 (최대 15초 대기)
                TestContext.WriteLine($"[Iteration {i}] STEP 2: Waiting for first data update...");
                var dataWaitTask = await Task.WhenAny(iterationDataReceived.Task, Task.Delay(15000, cts.Token));

                if (dataWaitTask != iterationDataReceived.Task)
                {
                    Assert.Fail($"[Iteration {i}] Timed out waiting for OrderBook data from {exchange}.");
                }

                TestContext.WriteLine($"[Iteration {i}] STEP 3: Data flowing. Monitoring for 5 seconds...");
                await Task.Delay(5000, cts.Token); // 데이터가 쌓이는 동안 잠시 관찰

                validationErrors.Should().BeEmpty($"OrderBook should be valid during active flow (Iteration {i})");

                TestContext.WriteLine($"[Iteration {i}] STEP 4: FORCING CONNECTION DROP!");
                if (adapter is BaseFeedAdapter baseAdapter)
                {
                    baseAdapter.SimulateConnectionDrop();
                }

                // 끊김 확인
                await Task.Delay(2000, cts.Token);
                adapter.IsConnected.Should().BeFalse($"Adapter should be disconnected (Iteration {i})");

                // 자동 재연결 대기
                TestContext.WriteLine($"[Iteration {i}] STEP 5: Waiting for auto-reconnect...");
                var reconnectTimeout = DateTime.UtcNow.AddSeconds(10);
                while (!adapter.IsConnected && DateTime.UtcNow < reconnectTimeout)
                {
                    await Task.Delay(1000, cts.Token);
                }

                adapter.IsConnected.Should().BeTrue($"Adapter must reconnect within timeout (Iteration {i})");

                // 루프가 반복되면서 다시 'STEP 1'의 SubscribeAsync로 이동하여 재구독 프로세스 검증
            }
        }
        finally
        {
            _marketDataManager.UnsubscribeOrderBook(instrument.InstrumentId, subscriberName);
        }

        // --- 3. Assert ---
        validationErrors.Should().BeEmpty("No integrity errors should occur across all reconnection and resubscription cycles.");
        TestContext.WriteLine("\n🎉 Chaos Test Passed Successfully.");
    }

    private ExchangeTopic GetOrderBookTopic(ExchangeEnum exchange)
    {
        switch (exchange)
        {
            case ExchangeEnum.BINANCE:
                return BinanceTopic.DepthUpdate;
            case ExchangeEnum.BITMEX:
                return BitmexTopic.OrderBook10;
            case ExchangeEnum.BITHUMB:
                return BithumbTopic.OrderBook;
            default:
                throw new InvalidOperationException($"Unsupported exchange: {exchange}");
        }
    }
}