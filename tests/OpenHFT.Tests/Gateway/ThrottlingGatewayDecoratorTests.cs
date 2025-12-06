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
        int limitPerSecond = 10;
        int requestCount = 12; // Send 12 requests, exceeding the limit of 10.
        var requests = new List<NewOrderRequest>();
        var tasks = new List<Task<OrderPlacementResult>>();

        var apiClient = _serviceProvider.GetRequiredService<BitmexRestApiClient>();
        var book = await BitmexOrderGatewayTests.GetOrderBookL2Async(apiClient, _btcUsdt.Symbol);
        var safeBidPrice = Price.FromDecimal(book.First(l => l.Side == "Buy").Price - 1000);

        for (int i = 0; i < requestCount; i++)
        {
            requests.Add(new NewOrderRequest(
                _btcUsdt.InstrumentId, GenerateClientOrderId(), Side.Buy,
                safeBidPrice - Price.FromDecimal(i * 10), // Unique price for each
                Quantity.FromDecimal(100m), OrderType.Limit, true
            ));
        }

        // Act
        TestContext.WriteLine($"Sending {requestCount} requests in a burst to a gateway with a limit of {limitPerSecond}/sec...");
        var stopwatch = Stopwatch.StartNew();

        foreach (var req in requests)
        {
            tasks.Add(_throttledGateway.SendNewOrderAsync(req));
        }

        // Wait for all immediate responses (success or rejection).
        var results = await Task.WhenAll(tasks);
        stopwatch.Stop();
        TestContext.WriteLine($"All {requestCount} requests received a response in {stopwatch.ElapsedMilliseconds} ms.");

        // Assert
        // 1. The operation should be very fast because there's no queueing/delay.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "because all requests should be accepted or rejected immediately without delay.");

        // 2. Count the number of successful and failed requests.
        int successCount = results.Count(r => r.IsSuccess);
        int failedCount = results.Count(r => !r.IsSuccess);

        TestContext.WriteLine($"Successful requests: {successCount}, Rejected requests: {failedCount}");

        // 3. Verify that exactly the limit number of requests succeeded.
        successCount.Should().Be(limitPerSecond,
            $"exactly {limitPerSecond} requests should have been accepted before the rate limit was hit.");

        // 4. Verify that the excess requests were rejected.
        failedCount.Should().Be(requestCount - limitPerSecond,
            $"the remaining {requestCount - limitPerSecond} requests should have been rejected.");

        // 5. Check the failure reason for one of the rejected requests.
        var firstRejected = results.First(r => !r.IsSuccess);
        firstRejected.FailureReason.Should().Be("Rate limit exceeded.");
    }
}

