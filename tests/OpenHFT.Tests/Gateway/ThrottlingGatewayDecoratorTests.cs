using System.Diagnostics;
using DotNetEnv;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using OpenHFT.Core.Api;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Gateway;
using OpenHFT.Gateway.ApiClient;

namespace OpenHFT.Tests.Gateway;

[TestFixture, Category("Integration")]
public class ThrottlingGatewayDecoratorTests
{
    private ServiceProvider _serviceProvider = null!;
    private string _testDirectory = null!;
    private IOrderGateway _throttledGateway = null!; // This will be the decorator instance
    private BitmexOrderGateway _realGateway = null!; // We need the real one for setup/cleanup
    private IInstrumentRepository _instrumentRepo = null!;
    private Instrument _btcUsdt = null!;

    private static long _clientOrderIdCounter = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private long GenerateClientOrderId() => Interlocked.Increment(ref _clientOrderIdCounter);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Env.Load();
        Env.TraversePath().Load();
        var apiKey = Environment.GetEnvironmentVariable("BITMEX_TESTNET_API_KEY");
        var apiSecret = Environment.GetEnvironmentVariable("BITMEX_TESTNET_API_SECRET");

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            Assert.Ignore("BITMEX_TESTNET_API_KEY or BITMEX_TESTNET_API_SECRET not set.");
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(new HttpClient());
        _testDirectory = Path.Combine(Path.GetTempPath(), "ThrottlingGatewayTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        var filePath = Path.Combine(_testDirectory, "instruments.csv");
        // Corrected CSV format
        var csvContent = @"instrument_id,market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
1,BITMEX,XBTUSDT,PerpetualFuture,XBT,USDT,0.1,0.0001,1,0.0001";
        File.WriteAllText(filePath, csvContent);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { { "dataFolder", _testDirectory } })
            .Build();

        services.AddSingleton(configuration);
        services.AddSingleton<IInstrumentRepository, InstrumentRepository>();

        // 1. Register the concrete REST API client
        services.AddSingleton(provider => new BitmexRestApiClient(
            provider.GetRequiredService<ILogger<BitmexRestApiClient>>(),
            provider.GetRequiredService<IInstrumentRepository>(),
            provider.GetRequiredService<HttpClient>(),
            ProductType.PerpetualFuture, ExecutionMode.Testnet, apiSecret, apiKey));

        // 2. Register the concrete Gateway implementation
        services.AddSingleton(provider => new BitmexOrderGateway(
            provider.GetRequiredService<ILogger<BitmexOrderGateway>>(),
            provider.GetRequiredService<BitmexRestApiClient>(),
            provider.GetRequiredService<IInstrumentRepository>(),
            ProductType.PerpetualFuture));

        // 3. Register the IOrderGateway interface to resolve to the Throttling DECORATOR
        services.AddSingleton<IOrderGateway>(provider =>
        {
            var realGateway = provider.GetRequiredService<BitmexOrderGateway>();
            var logger = provider.GetRequiredService<ILogger<ThrottlingGatewayDecorator>>();

            // BitMEX limits (slightly stricter for reliable testing)
            // Use 10 reqs/sec for this test
            var perSecondConfig = new RateLimiterConfig(Limit: 10, Window: TimeSpan.FromSeconds(1));
            var perMinuteConfig = new RateLimiterConfig(Limit: 120, Window: TimeSpan.FromMinutes(1));

            return new ThrottlingGatewayDecorator(realGateway, logger, perSecondConfig, perMinuteConfig);
        });

        _serviceProvider = services.BuildServiceProvider();
        _throttledGateway = _serviceProvider.GetRequiredService<IOrderGateway>();
        _realGateway = _serviceProvider.GetRequiredService<BitmexOrderGateway>();
        _instrumentRepo = _serviceProvider.GetRequiredService<IInstrumentRepository>();
        _btcUsdt = _instrumentRepo.FindBySymbol("XBTUSDT", ProductType.PerpetualFuture, ExchangeEnum.BITMEX);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        TestContext.WriteLine("Cleaning up: Cancelling all open orders for XBTUSDT...");
        await _realGateway.CancelAllOrdersAsync(_btcUsdt.Symbol);

        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Test, Order(1)]
    public async Task ThrottlesRequests_WhenBurstExceedsLimit()
    {
        // Arrange
        // We'll send 12 requests, which is more than the 10 req/sec limit.
        int requestCount = 24;
        var requests = new List<NewOrderRequest>();
        var tasks = new List<Task<OrderPlacementResult>>();

        var apiClient = _serviceProvider.GetRequiredService<BitmexRestApiClient>();
        var book = await BitmexOrderGatewayTests.GetOrderBookL2Async(apiClient, _btcUsdt.Symbol);
        var safeBidPrice = Price.FromDecimal(book.First(l => l.Side == "Buy").Price - 10000);

        for (int i = 0; i < requestCount; i++)
        {
            requests.Add(new NewOrderRequest(
                _btcUsdt.InstrumentId, GenerateClientOrderId(), Side.Buy,
                safeBidPrice - Price.FromDecimal(i * 10), // Unique price for each
                Quantity.FromDecimal(100m), OrderType.Limit, true
            ));
        }

        // Act
        TestContext.WriteLine($"Sending {requestCount} requests in a burst...");
        var stopwatch = Stopwatch.StartNew();
        foreach (var req in requests)
        {
            tasks.Add(_throttledGateway.SendNewOrderAsync(req));
        }
        var results = await Task.WhenAll(tasks);
        stopwatch.Stop();
        TestContext.WriteLine($"All {requestCount} requests completed in {stopwatch.ElapsedMilliseconds} ms.");

        // Assert
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1),
            "because the 12 requests should have been throttled across at least two 1-second windows.");

        results.All(r => r.IsSuccess).Should().BeTrue("all requests should eventually succeed.");
    }

    [Test, Order(2)]
    public async Task ReplaceThenCancel_SendsOnlyCancelRequest_AndCancelsPreviousTask()
    {
        // --- Arrange ---
        // 1. Create a live order to modify
        var clientOrderId = GenerateClientOrderId();
        var apiClient = _serviceProvider.GetRequiredService<BitmexRestApiClient>();
        var book = await BitmexOrderGatewayTests.GetOrderBookL2Async(apiClient, _btcUsdt.Symbol);
        var safeBidPrice = Price.FromDecimal(book.First(l => l.Side == "Buy").Price - 500);
        var newOrderRequest = new NewOrderRequest(_btcUsdt.InstrumentId, clientOrderId, Side.Buy, safeBidPrice, Quantity.FromDecimal(100m), OrderType.Limit, true);

        var placementResult = await _realGateway.SendNewOrderAsync(newOrderRequest);
        placementResult.IsSuccess.Should().BeTrue();
        var exchangeOrderId = placementResult.InitialReport.Value.ExchangeOrderId;

        // 2. Setup mock and decorator for verification
        var mockGateway = new Mock<IOrderGateway>();
        mockGateway.Setup(g => g.SendCancelOrderAsync(It.IsAny<CancelOrderRequest>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new OrderModificationResult(true, null, null));

        var logger = _serviceProvider.GetRequiredService<ILogger<ThrottlingGatewayDecorator>>();
        using var testDecorator = new ThrottlingGatewayDecorator(
            mockGateway.Object, logger,
            new RateLimiterConfig(10, TimeSpan.FromSeconds(1)),
            new RateLimiterConfig(120, TimeSpan.FromMinutes(1))
        );

        // --- Act ---
        var newPrice = safeBidPrice - Price.FromDecimal(10);
        var replaceRequest = new ReplaceOrderRequest(exchangeOrderId, newPrice, _btcUsdt.InstrumentId);
        var cancelRequest = new CancelOrderRequest(exchangeOrderId, _btcUsdt.InstrumentId);

        TestContext.WriteLine("Queueing Replace request (will be superseded)...");
        var replaceTask = testDecorator.SendReplaceOrderAsync(replaceRequest);

        TestContext.WriteLine("Queueing Cancel request immediately after...");
        var cancelTask = testDecorator.SendCancelOrderAsync(cancelRequest);

        // FIX: Instead of a fixed delay, wait for the tasks to complete.
        // The superseded task (replaceTask) should complete with a Canceled status.
        // The final task (cancelTask) should complete successfully.
        try
        {
            // Wait for both tasks. WhenAll will throw if any task faults or is canceled.
            await Task.WhenAll(replaceTask, cancelTask);
        }
        catch (OperationCanceledException)
        {
            // This is the expected exception when WhenAll encounters a canceled task.
            // We can safely ignore it and proceed with assertions.
            TestContext.WriteLine("Task.WhenAll correctly threw OperationCanceledException as replaceTask was canceled.");
        }

        // --- Assert ---
        // Verify that only the Cancel method was called on the underlying gateway
        mockGateway.Verify(g => g.SendReplaceOrderAsync(It.IsAny<ReplaceOrderRequest>(), It.IsAny<CancellationToken>()),
            Times.Never(), "The replace request should have been superseded and never sent.");

        mockGateway.Verify(g => g.SendCancelOrderAsync(It.IsAny<CancelOrderRequest>(), It.IsAny<CancellationToken>()),
            Times.Once(), "The final cancel request should have been sent exactly once.");

        // Verify the state of each task individually
        replaceTask.IsCanceled.Should().BeTrue("the superseded task must be put into a Canceled state.");
        cancelTask.IsCompletedSuccessfully.Should().BeTrue("the final cancel task should complete successfully.");

        // You can also check the result of the successful task
        var cancelResult = await cancelTask;
        cancelResult.IsSuccess.Should().BeTrue();

        TestContext.WriteLine("Verification successful: Only the cancel request was sent.");
    }
}

