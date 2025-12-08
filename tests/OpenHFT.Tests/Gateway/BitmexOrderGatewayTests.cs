using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenHFT.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Gateway.ApiClient;
using OpenHFT.Gateway;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using FluentAssertions;
using DotNetEnv;

namespace OpenHFT.Tests.Gateway;

[TestFixture, Category("Integration")]
public class BitmexOrderGatewayTests
{
    private ServiceProvider _serviceProvider = null!;
    private string _testDirectory = null!;
    private IOrderGateway _gateway;
    private IInstrumentRepository _instrumentRepo;
    private Instrument _btcUsdt;

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
            Assert.Ignore("BITMEX_TESTNET_API_KEY or BITMEX_TESTNET_API_SECRET not set");
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(new HttpClient());
        _testDirectory = Path.Combine(Path.GetTempPath(), "InstrumentRepositoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        var filePath = Path.Combine(_testDirectory, "instruments.csv");
        var csvContent = @"instrument_id,market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
    1,BITMEX,XBTUSDT,perpetualfuture,XBTUSDT,USDT,1,0.1,100,BTCUSDT,0.0001";
        File.WriteAllText(filePath, csvContent);

        var inMemorySettings = new Dictionary<string, string>
        {
            { "dataFolder", _testDirectory }
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        services.AddSingleton(configuration);
        services.AddSingleton<IInstrumentRepository, InstrumentRepository>();
        services.AddSingleton(provider =>
        {
            var client = new BitmexRestApiClient(provider.GetRequiredService<ILogger<BitmexRestApiClient>>(),
                provider.GetRequiredService<IInstrumentRepository>(),
                provider.GetRequiredService<HttpClient>(),
                ProductType.PerpetualFuture,
                ExecutionMode.Testnet,
                apiSecret,
                apiKey);
            return client;

        });
        services.AddSingleton<IOrderGateway, BitmexOrderGateway>(provider =>
            new BitmexOrderGateway(provider.GetRequiredService<ILogger<BitmexOrderGateway>>(),
                provider.GetRequiredService<BitmexRestApiClient>(),
                provider.GetRequiredService<IInstrumentRepository>(),
                ProductType.PerpetualFuture)
        );
        _serviceProvider = services.BuildServiceProvider();
        _gateway = _serviceProvider.GetRequiredService<IOrderGateway>();
        _instrumentRepo = _serviceProvider.GetRequiredService<IInstrumentRepository>();
        _btcUsdt = _instrumentRepo.FindBySymbol("XBTUSDT", ProductType.PerpetualFuture, ExchangeEnum.BITMEX);
    }

    public static async Task<IReadOnlyList<BitmexOrderBookL2>> GetOrderBookL2Async(BitmexRestApiClient client, string symbol, int depth = 1, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/api/v1/orderbook/L2?symbol={symbol.ToUpper()}&depth={depth}";
        var result = await client.SendRequestAsync<IReadOnlyList<BitmexOrderBookL2>>(HttpMethod.Get, endpoint, cancellationToken: cancellationToken);
        if (!result.IsSuccess)
        {
            throw result.Error;
        }

        return result.Data;
    }

    [Test, Order(1), Category("Lifecycle")]
    public async Task OrderLifecycle_Submit_Amend_Cancel_ShouldSucceed()
    {
        // --- 1. 신규 주문 (Submit) ---
        var clientOrderId = GenerateClientOrderId();

        // 현재 시장가에서 멀리 떨어진 가격에 지정가 주문을 넣어 즉시 체결되지 않도록 함
        var apiClient = _serviceProvider.GetRequiredService<BitmexRestApiClient>();
        var book = await GetOrderBookL2Async(apiClient, _btcUsdt.Symbol);
        var safeBidPrice = Price.FromDecimal(book.First(l => l.Side == "Buy").Price - 500);

        var newOrderRequest = new NewOrderRequest(_btcUsdt.InstrumentId, clientOrderId, Side.Buy, safeBidPrice, Quantity.FromDecimal(100m), OrderType.Limit, true);

        // Act
        var placementResult = await _gateway.SendNewOrderAsync(newOrderRequest);

        // Assert
        placementResult.IsSuccess.Should().BeTrue(because: placementResult.FailureReason);
        placementResult.InitialReport.Should().NotBeNull();
        placementResult.InitialReport!.Value.Status.Should().Be(OrderStatus.New);
        var exchangeOrderId = placementResult.InitialReport!.Value.ExchangeOrderId;
        exchangeOrderId.Should().NotBeNullOrEmpty();

        // 잠시 대기
        await Task.Delay(2000);
        // --- 2. 주문 정정 (Amend/Replace) ---
        var newPrice = safeBidPrice - Price.FromDecimal(10); // 가격을 10달러 더 낮춤
        var replaceRequest = new ReplaceOrderRequest(exchangeOrderId, newPrice, _btcUsdt.InstrumentId);
        // Act
        var modificationResult = await _gateway.SendReplaceOrderAsync(replaceRequest);

        // Assert
        modificationResult.IsSuccess.Should().BeTrue(because: modificationResult.FailureReason);

        // 잠시 대기
        await Task.Delay(2000);

        // --- 3. 주문 취소 (Cancel) ---
        var cancelRequest = new CancelOrderRequest(exchangeOrderId, _btcUsdt.InstrumentId);

        // Act
        var cancelResult = await _gateway.SendCancelOrderAsync(cancelRequest);

        // Assert
        cancelResult.IsSuccess.Should().BeTrue(because: cancelResult.FailureReason);
    }

    [Test, Order(2), Category("Lifecycle")]
    public async Task BulkCancel_ShouldSucceed_ForMultipleOrders()
    {
        // --- 1. Arrange: Create multiple live orders to cancel ---
        int ordersToCreate = 3;
        var orderIdsToCancel = new List<string>();
        var tasks = new List<Task<OrderPlacementResult>>();

        var apiClient = _serviceProvider.GetRequiredService<BitmexRestApiClient>();
        var book = await GetOrderBookL2Async(apiClient, _btcUsdt.Symbol);
        var safeBidPrice = Price.FromDecimal(book.First(l => l.Side == "Buy").Price - 1000);

        TestContext.WriteLine($"Creating {ordersToCreate} orders to be cancelled in bulk...");

        for (int i = 0; i < ordersToCreate; i++)
        {
            var newOrderRequest = new NewOrderRequest(
                _btcUsdt.InstrumentId,
                GenerateClientOrderId(),
                Side.Buy,
                safeBidPrice - Price.FromDecimal(i * 10), // Use slightly different prices
                Quantity.FromDecimal(100m),
                OrderType.Limit,
                true);

            tasks.Add(_gateway.SendNewOrderAsync(newOrderRequest));
        }

        var placementResults = await Task.WhenAll(tasks);

        foreach (var result in placementResults)
        {
            result.IsSuccess.Should().BeTrue("Order placement must succeed for setup.");
            result.InitialReport.Should().NotBeNull();
            orderIdsToCancel.Add(result.InitialReport!.Value.ExchangeOrderId!);
        }

        orderIdsToCancel.Should().HaveCount(ordersToCreate, "All orders should be created successfully.");
        TestContext.WriteLine($"Successfully created orders: {string.Join(", ", orderIdsToCancel)}");

        // Give the exchange a moment to process the new orders
        await Task.Delay(1000);

        // --- 2. Act: Send the bulk cancel request ---
        TestContext.WriteLine("Sending bulk cancel request...");
        var bulkCancelRequest = new BulkCancelOrdersRequest(orderIdsToCancel, _btcUsdt.InstrumentId);
        var results = await _gateway.SendBulkCancelOrdersAsync(bulkCancelRequest);

        // --- 3. Assert: Verify the results of the bulk cancellation ---
        results.Should().NotBeNull();
        results.Should().HaveCount(ordersToCreate, "the result list should contain an entry for each requested order.");

        // Check if the set of IDs in the response matches the set of IDs we sent
        results.Select(r => r.OrderId).Should().BeEquivalentTo(orderIdsToCancel,
            "the response should contain results for all the orders we requested to cancel.");

        // Check each individual result
        foreach (var result in results)
        {
            result.IsSuccess.Should().BeTrue(because: $"order {result.OrderId} should have been cancelled successfully.");
            result.Report.Should().NotBeNull();
            result.Report!.Value.Status.Should().Be(OrderStatus.Cancelled,
                because: $"the status for order {result.OrderId} should be reported as Cancelled.");
        }

        TestContext.WriteLine("Bulk cancellation successful and all results verified.");
    }

    // [Test, Order(2), Category("Execution")]
    // public async Task NewOrder_TakerOrder_ShouldFillImmediately()
    // {
    //     // Arrange
    //     var clientOrderId = GenerateClientOrderId();

    //     // 시장가보다 높은 가격에 매수 주문을 넣어 즉시 체결(Taker)되도록 함
    //     var apiClient = _serviceProvider.GetRequiredService<BitmexRestApiClient>();
    //     var book = await GetOrderBookL2Async(apiClient, _btcUsdt.Symbol);
    //     var takerBidPrice = Price.FromDecimal(book.First(l => l.Side == "Sell").Price + 10);

    //     var takerOrderRequest = new NewOrderRequest(_btcUsdt.InstrumentId, clientOrderId, Side.Buy, takerBidPrice, Quantity.FromDecimal(100m), OrderType.Limit, false);

    //     // Act
    //     var placementResult = await _gateway.SendNewOrderAsync(takerOrderRequest);

    //     // Assert
    //     placementResult.IsSuccess.Should().BeTrue(because: placementResult.FailureReason);
    //     placementResult.InitialReport.Should().NotBeNull();

    //     // 즉시 체결되었으므로 상태는 'Filled'여야 함
    //     placementResult.InitialReport!.Value.Status.Should().Be(OrderStatus.Filled);
    // }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

}
