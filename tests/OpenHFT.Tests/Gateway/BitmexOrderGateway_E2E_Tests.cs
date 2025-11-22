using System;
using Disruptor.Dsl;
using DotNetEnv;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Feed;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Gateway;
using OpenHFT.Gateway.ApiClient;
using OpenHFT.Processing;
using OpenHFT.Tests.Processing;

namespace OpenHFT.Tests.Gateway;

[TestFixture, Category("E2E_Integration")]
public class BitmexOrderGateway_E2E_Tests
{
    private ServiceProvider _serviceProvider = null!;
    private IFeedHandler _feedHandler = null!;
    private IOrderGateway _gateway = null!;
    private IOrderRouter _orderRouter = null!;
    private IInstrumentRepository _instrumentRepo = null!;
    private Instrument _btcUsdt = null!;
    private IOrderFactory _orderFactory = null!;
    private string _testDirectory = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
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
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddDebug();
        });
        services.AddSingleton(new HttpClient());

        _testDirectory = Path.Combine(Path.GetTempPath(), "InstrumentRepositoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        var filePath = Path.Combine(_testDirectory, "instruments.csv");
        var csvContent = @"instrument_id,market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
    1,BITMEX,XBTUSDT,perpetualfuture,XBTUSDT,USDT,1,0.1,100,XBTUSDT,0.0001";
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
        services.AddSingleton<IOrderUpdateHandler, OrderUpdateDistributor>();
        services.AddSingleton<MarketDataDistributor>();
        services.AddSingleton(provider =>
        {
            var disruptor = new Disruptor<OrderStatusReportWrapper>(() => new OrderStatusReportWrapper(), 1024);
            disruptor.HandleEventsWith(provider.GetRequiredService<IOrderUpdateHandler>());
            return disruptor;
        });
        services.AddSingleton(provider =>
        {
            var disruptor = new Disruptor<MarketDataEventWrapper>(() => new MarketDataEventWrapper(), 1024);
            disruptor.HandleEventsWith(provider.GetRequiredService<MarketDataDistributor>());
            return disruptor;
        });
        services.AddSingleton<IOrderRouter, OrderRouter>();
        services.AddSingleton<IFeedHandler, FeedHandler>();
        services.AddSingleton<IFeedAdapter>(provider =>
                                new BitmexAdapter(
                                provider.GetRequiredService<ILogger<BitmexAdapter>>(),
                                ProductType.PerpetualFuture,
                                provider.GetRequiredService<IInstrumentRepository>(),
                                ExecutionMode.Testnet
                            ));
        services.AddSingleton<IFeedAdapterRegistry, FeedAdapterRegistry>();
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
        services.AddSingleton<IOrderGatewayRegistry, OrderGatewayRegistry>();
        services.AddSingleton<IOrderFactory, OrderFactory>();
        _serviceProvider = services.BuildServiceProvider();

        // --- start services ---
        _orderFactory = _serviceProvider.GetRequiredService<IOrderFactory>();
        _gateway = _serviceProvider.GetRequiredService<IOrderGateway>();
        _instrumentRepo = _serviceProvider.GetRequiredService<IInstrumentRepository>();
        _orderRouter = _serviceProvider.GetRequiredService<IOrderRouter>();
        _feedHandler = _serviceProvider.GetRequiredService<IFeedHandler>();
        _btcUsdt = _instrumentRepo.FindBySymbol("XBTUSDT", ProductType.PerpetualFuture, ExchangeEnum.BITMEX);
        var marketDisruptor = _serviceProvider.GetRequiredService<Disruptor<MarketDataEventWrapper>>();
        var orderDisruptor = _serviceProvider.GetRequiredService<Disruptor<OrderStatusReportWrapper>>();
        marketDisruptor.Start();
        orderDisruptor.Start();

        var adapter = _serviceProvider.GetServices<IFeedAdapter>().OfType<BitmexAdapter>().FirstOrDefault();
        await adapter.ConnectAsync();
        await adapter.AuthenticateAsync(apiKey, apiSecret);
        await adapter.SubscribeToPrivateTopicsAsync(CancellationToken.None);
    }

    [Test, Order(1), Category("E2E_Lifecycle")]
    public async Task E2E_OrderLifecycle_ShouldReceiveStatusUpdatesViaWebSocket()
    {
        // --- 1. 신규 주문 (Submit) 및 'New' 상태 수신 대기 ---
        var orderBuilder = new OrderBuilder(_orderFactory, _btcUsdt.InstrumentId, Side.Buy, "test");

        // 오더북에서 안전한 가격 찾기
        var apiClient = _serviceProvider.GetRequiredService<BitmexRestApiClient>();
        var book = await BitmexOrderGatewayTests.GetOrderBookL2Async(apiClient, _btcUsdt.Symbol);
        var safeBidPrice = Price.FromDecimal(book.First(l => l.Side == "Buy").Price - 500);
        var order = orderBuilder.WithPrice(safeBidPrice).WithQuantity(Quantity.FromDecimal(100m)).Build();

        // Act: 주문 제출 (REST API)
        await order.SubmitAsync();

        await Task.Delay(3000);

        // Assert: WebSocket을 통해 'New' 리포트를 받았는지 확인
        order.Status.Should().Be(OrderStatus.New, "because the order should be confirmed via WebSocket after submission.");
        order.ExchangeOrderId.Should().NotBeNullOrEmpty();
        var exchangeOrderId = order.ExchangeOrderId;
        TestContext.WriteLine($" -> Success. Order is 'New'. EXO: {exchangeOrderId}");

        // --- 2. 주문 정정 (Amend) 및 가격 변경 수신 대기 ---
        var newPrice = safeBidPrice - Price.FromDecimal(10m);

        // Act: 정정 주문 제출 (REST API)
        await order.ReplaceAsync(newPrice, OrderType.Limit);

        await Task.Delay(3000);
        // Assert: WebSocket을 통해 가격이 변경된 리포트를 받았는지 확인
        order.Price.Should().Be(newPrice, "because the order replacement should have updated the price.");
        TestContext.WriteLine($" -> Success. Order price amended to {order.Price}.");
        TestContext.WriteLine($"Order successfully amended to new price: {newPrice}");

        // --- 3. 주문 취소 (Cancel) 및 'Cancelled' 상태 수신 대기 ---

        // Act: 취소 주문 제출 (REST API)
        await order.CancelAsync();

        await Task.Delay(3000);
        // Assert: WebSocket을 통해 'Cancelled' 리포트를 받았는지 확인
        order.Status.Should().Be(OrderStatus.Cancelled, "because the cancellation request should be confirmed via WebSocket.");
        TestContext.WriteLine("Order successfully cancelled.");
    }

    /// <summary>
    /// Tests that a taker order is immediately filled and the Order object's state
    /// is correctly updated via the WebSocket feedback loop.
    /// </summary>
    // [Test, Order(2), Category("E2E_Execution")]
    // public async Task E2E_TakerOrder_ShouldUpdateStatusToFilled()
    // {
    //     // --- Arrange ---
    //     var orderBuilder = new OrderBuilder(_orderFactory, _btcUsdt.InstrumentId, Side.Buy);

    //     // 시장의 매도 호가보다 높은 가격에 매수 주문을 넣어 즉시 체결(Taker)되도록 합니다.
    //     var apiClient = _serviceProvider.GetRequiredService<BitmexRestApiClient>();
    //     var book = await BitmexOrderGatewayTests.GetOrderBookL2Async(apiClient, _btcUsdt.Symbol);
    //     var takerBidPrice = Price.FromDecimal(book.First(l => l.Side == "Sell").Price);
    //     var orderQuantity = Quantity.FromDecimal(100m); // BitMEX XBTUSDT의 최소 주문 수량

    //     // IOrderBuilder를 사용하여 Order 객체 생성
    //     var order = (Order)orderBuilder
    //         .WithPrice(takerBidPrice)
    //         .WithQuantity(orderQuantity)
    //         .WithOrderType(OrderType.Limit)
    //         .WithPostOnly(false)
    //         .Build();

    //     TestContext.WriteLine($"Step 1: Submitting TAKER order to be filled. CID: {order.ClientOrderId}, Price: {takerBidPrice}, Qty: {orderQuantity}");

    //     // --- Act ---
    //     await order.SubmitAsync();

    //     await Task.Delay(3000);

    //     // --- Assert ---
    //     // --- Assert ---
    //     TestContext.WriteLine($"Asserting order status after waiting. Current state: {order}");

    //     // 1. 체결 내역이 하나 이상 있는지 확인합니다.
    //     order.Fills.Should().NotBeEmpty("because the taker order should have been at least partially filled.");

    //     // 2. 누적 체결량이 0보다 큰지 확인합니다.
    //     var cumFills = order.Fills.Sum(a => a.Quantity.ToDecimal());
    //     cumFills.Should().BeGreaterThan(0m, "because at least one fill should have occurred.");

    //     // 3. 주문 상태가 PartiallyFilled 또는 Filled 인지 확인합니다.
    //     order.Status.Should().BeOneOf(OrderStatus.PartiallyFilled, OrderStatus.Filled);

    //     TestContext.WriteLine($" -> Success. Received {order.Fills.Count} fill(s). " +
    //                           $"Cumulative Qty: {cumFills}, Final Status: '{order.Status}'.");

    //     // 테스트 정리: 남은 주문이 있다면 취소
    //     if (order.Status == OrderStatus.PartiallyFilled)
    //     {
    //         await order.CancelAsync();
    //         await Task.Delay(2000); // 취소 확인 대기
    //     }
    // }

    /// <summary>
    /// Tests that a Post-Only order is correctly REJECTED if it would execute immediately.
    /// </summary>
    // [Test, Order(3)]
    // public async Task E2E_PostOnlyTakerOrder_ShouldBeRejected()
    // {
    //     // --- Arrange ---
    //     // IOrderFactory를 통해 'Order'의 기본 구현체를 생성합니다.
    //     var orderBuilder = new OrderBuilder(_orderFactory, _btcUsdt.InstrumentId, Side.Buy);

    //     // 시장의 매도 호가와 "동일한" 가격에 매수 주문을 넣어 즉시 체결될 상황을 만듭니다.
    //     // Post-Only는 가격이 크로스되거나 같을 때 거부됩니다.
    //     var takerBidPrice = await GetTakerPriceForPostOnlyTest(Side.Buy);
    //     var orderQuantity = Quantity.FromDecimal(100m);

    //     // IOrderBuilder를 사용하여 PostOnly 플래그가 설정된 Order 객체를 생성합니다.
    //     var order = (Order)orderBuilder
    //         .WithPrice(takerBidPrice)
    //         .WithQuantity(orderQuantity)
    //         .WithOrderType(OrderType.Limit)
    //         .WithPostOnly(true) // <-- 여기가 핵심입니다!
    //         .Build();

    //     TestContext.WriteLine($"Step 1: Submitting POST-ONLY taker order. CID: {order.ClientOrderId}, Price: {takerBidPrice}, Qty: {orderQuantity}");

    //     // --- Act ---
    //     // 주문을 제출합니다. 이 API 호출 자체는 성공해야 합니다 (요청이 잘 전달됨).
    //     await order.SubmitAsync();

    //     // WebSocket을 통해 'Rejected' 상태 업데이트가 도착할 시간을 기다립니다.
    //     // 또는 API 응답이 즉시 실패를 반환하는 경우도 있습니다.
    //     await Task.Delay(3000);

    //     // --- Assert ---
    //     TestContext.WriteLine($"Asserting order status after delay. Current state: {order}");

    //     // 1. 최종 주문 상태가 'Rejected'인지 확인합니다.
    //     order.Status.Should().Be(OrderStatus.Cancelled, "because a Post-Only order that crosses the spread must be rejected.");

    //     // 2. 체결 내역은 비어 있어야 합니다.
    //     order.Fills.Should().BeEmpty("because a rejected order cannot have fills.");

    //     // 3. 남은 수량은 원래 주문 수량과 동일해야 합니다.
    //     order.LeavesQuantity.Should().Be(Quantity.FromDecimal(0m), "because a canceled order has no leaves quantity.");

    //     // 4. (선택적) 거부 사유 확인
    //     order.LatestReport.Should().NotBeNull();

    //     TestContext.WriteLine($" -> Success. Final order status is '{order.Status}' as expected.");
    // }

    // private async Task<Price> GetTakerPriceForPostOnlyTest(Side side)
    // {
    //     var apiClient = _serviceProvider.GetRequiredService<BitmexRestApiClient>();
    //     var book = await BitmexOrderGatewayTests.GetOrderBookL2Async(apiClient, _btcUsdt.Symbol);
    //     if (side == Side.Buy)
    //     {
    //         // 매수 주문의 경우, 시장의 가장 낮은 매도 호가와 "같거나 높은" 가격을 설정
    //         var bestAskPrice = book.FirstOrDefault(l => l.Side == "Sell")?.Price;
    //         if (bestAskPrice == null) Assert.Fail("Could not get best ask price from testnet.");
    //         return Price.FromDecimal(bestAskPrice.Value);
    //     }
    //     else // side == Side.Sell
    //     {
    //         // 매도 주문의 경우, 시장의 가장 높은 매수 호가와 "같거나 낮은" 가격을 설정
    //         var bestBidPrice = book.FirstOrDefault(l => l.Side == "Buy")?.Price;
    //         if (bestBidPrice == null) Assert.Fail("Could not get best bid price from testnet.");
    //         return Price.FromDecimal(bestBidPrice.Value);
    //     }
    // }
}
