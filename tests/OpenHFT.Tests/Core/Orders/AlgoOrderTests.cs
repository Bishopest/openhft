using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;

namespace OpenHFT.Tests.Core.Orders;

[TestFixture]
public class AlgoOrderTests
{
    private Mock<IOrderRouter> _mockRouter;
    private Mock<IOrderGateway> _mockGateway;
    private Mock<IMarketDataManager> _mockMarketData;
    private Mock<ILogger<Order>> _mockLogger;

    private Instrument _instrument;
    private const int InstrumentId = 1;
    private const string BookName = "TestBook";

    [SetUp]
    public void Setup()
    {
        _mockRouter = new Mock<IOrderRouter>();
        _mockGateway = new Mock<IOrderGateway>();
        _mockMarketData = new Mock<IMarketDataManager>();
        _mockLogger = new Mock<ILogger<Order>>();

        // TickSize 1, LotSize 1 인 테스트용 종목
        _instrument = new CryptoPerpetual(InstrumentId, "BTCUSDT", ExchangeEnum.BINANCE, Currency.BTC, Currency.USDT, Price.FromDecimal(1m), Quantity.FromDecimal(1m), 1m, Quantity.FromDecimal(1m));
        // Gateway Mock Setup (성공 응답 기본값)
        _mockGateway.Setup(g => g.SendReplaceOrderAsync(It.IsAny<ReplaceOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderModificationResult(true, "ExId_1", null, null));

        _mockGateway.Setup(g => g.SendNewOrderAsync(It.IsAny<NewOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderPlacementResult(true, "ExId_1", null, null));
    }

    #region Helper Methods

    private OrderBook CreateOrderBook(decimal bestBid, decimal bestAsk)
    {
        var book = new OrderBook(_instrument);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Populate Bid
        if (bestBid > 0)
        {
            var updates = new PriceLevelEntryArray();
            updates[0] = new PriceLevelEntry(Side.Buy, bestBid, 10m);
            book.ApplyEvent(new MarketDataEvent(1, ts, EventKind.Snapshot, InstrumentId, ExchangeEnum.BINANCE, 0, 0, 1, updates));
        }

        // Populate Ask
        if (bestAsk > 0)
        {
            var updates = new PriceLevelEntryArray();
            updates[0] = new PriceLevelEntry(Side.Sell, bestAsk, 10m);
            book.ApplyEvent(new MarketDataEvent(2, ts, EventKind.Update, InstrumentId, ExchangeEnum.BINANCE, 0, 0, 1, updates));
        }

        return book;
    }

    private void SimulateOrderActive(AlgoOrder order)
    {
        // AlgoOrder는 상태가 New가 되어야 로직이 활성화됨
        // 1. ExchangeId 설정
        var report1 = new OrderStatusReport(order.ClientOrderId, "ExId_1", null, InstrumentId, order.Side, OrderStatus.Pending, order.Price, order.Quantity, order.Quantity, 0);
        order.OnStatusReportReceived(report1);

        // 2. New 상태 수신 -> IsAlgoRunning = true
        var report2 = new OrderStatusReport(order.ClientOrderId, "ExId_1", null, InstrumentId, order.Side, OrderStatus.New, order.Price, order.Quantity, order.Quantity, 0);
        order.OnStatusReportReceived(report2);
    }

    #endregion

    #region OppositeFirstHedgeOrder Tests

    [Test]
    public void OppositeFirst_Buy_ShouldTargetBestAsk()
    {
        // Arrange
        var order = new OppositeFirstOrder(1001, InstrumentId, Side.Buy, BookName, _mockRouter.Object, _mockGateway.Object, _mockLogger.Object, _mockMarketData.Object);
        order.Price = Price.FromDecimal(90); // 현재 내 주문가
        SimulateOrderActive(order);

        var book = CreateOrderBook(bestBid: 95, bestAsk: 100);

        // Act
        order.OnMarketDataUpdated(book);

        // Assert
        // Buy 주문이므로 상대(Sell) 최우선 호가인 100으로 정정해야 함
        _mockGateway.Verify(g => g.SendReplaceOrderAsync(
            It.Is<ReplaceOrderRequest>(r => r.NewPrice.ToDecimal() == 100m),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task OppositeFirst_Sell_ShouldTargetBestBidAsync()
    {
        // Arrange
        var order = new OppositeFirstOrder(1002, InstrumentId, Side.Sell, BookName, _mockRouter.Object, _mockGateway.Object, _mockLogger.Object, _mockMarketData.Object);
        order.Price = Price.FromDecimal(110);
        SimulateOrderActive(order);

        var book = CreateOrderBook(bestBid: 100, bestAsk: 105);

        var tcs = new TaskCompletionSource<bool>();

        _mockGateway.Setup(g => g.SendReplaceOrderAsync(It.IsAny<ReplaceOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderModificationResult(true, "ExId_1", null, null))
            .Callback(() => tcs.TrySetResult(true)); // 호출 감지 시 완료 처리

        // Act
        order.OnMarketDataUpdated(book); // 내부에서 Fire-and-Forget으로 ReplaceAsync 실행됨

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(1000));

        if (completedTask != tcs.Task)
        {
            Assert.Fail("Timeout: SendReplaceOrderAsync was not called within the expected time.");
        }

        _mockGateway.Verify(g => g.SendReplaceOrderAsync(
            It.Is<ReplaceOrderRequest>(r => r.NewPrice.ToDecimal() == 100m),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region FirstFollowHedgeOrder Tests

    [Test]
    public async Task FirstFollow_Buy_ShouldTargetBestBidPlusTick_WhenNotLeadingAsync()
    {
        // Arrange
        // (참고: 이전 대화에서 클래스명을 FirstFollowHedgeOrder로 정의했으므로 이를 사용합니다)
        var order = new FirstFollowOrder(2001, _instrument, Side.Buy, BookName, _mockRouter.Object, _mockGateway.Object, _mockLogger.Object, _mockMarketData.Object);
        order.Price = Price.FromDecimal(90); // 내 가격
        SimulateOrderActive(order);

        var book = CreateOrderBook(bestBid: 95, bestAsk: 100); // 시장 1호가가 95

        // [핵심 1] 비동기 호출 감지용 신호등(TCS) 생성
        var tcs = new TaskCompletionSource<bool>();

        // [핵심 2] Gateway Mock 설정: 호출 시 tcs 완료 처리
        _mockGateway.Setup(g => g.SendReplaceOrderAsync(It.IsAny<ReplaceOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderModificationResult(true, "ExId_1", null, null))
            .Callback(() => tcs.TrySetResult(true));

        // Act
        order.OnMarketDataUpdated(book); // 내부에서 별도 스레드풀 작업으로 ReplaceAsync가 실행됨

        // [핵심 3] 실제 호출이 일어날 때까지 최대 1초 대기 (Race Condition 방지)
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(1000));

        if (completedTask != tcs.Task)
        {
            Assert.Fail("Timeout: SendReplaceOrderAsync was not called within the expected time.");
        }

        // Assert
        // 시장가(95)가 나(90)보다 높으므로, 95 + 1(Tick) = 96으로 따라가야 함
        _mockGateway.Verify(g => g.SendReplaceOrderAsync(
            It.Is<ReplaceOrderRequest>(r => r.NewPrice.ToDecimal() == 96m),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void FirstFollow_Buy_ShouldStay_WhenAlreadyLeading()
    {
        // Arrange
        var order = new FirstFollowOrder(2002, _instrument, Side.Buy, BookName, _mockRouter.Object, _mockGateway.Object, _mockLogger.Object, _mockMarketData.Object);
        order.Price = Price.FromDecimal(96); // 내 가격 (이미 1등)
        SimulateOrderActive(order);

        var book = CreateOrderBook(bestBid: 96, bestAsk: 100); // 시장 1호가가 나(96)

        // Act
        order.OnMarketDataUpdated(book);

        // Assert
        // Self-Pennying 방지: 내가 이미 BestBid이므로 가격을 올리지 않고 유지해야 함
        _mockGateway.Verify(g => g.SendReplaceOrderAsync(It.IsAny<ReplaceOrderRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void FirstFollow_Sell_ShouldTargetBestAskMinusTick_WhenNotLeading()
    {
        // Arrange
        var order = new FirstFollowOrder(2003, _instrument, Side.Sell, BookName, _mockRouter.Object, _mockGateway.Object, _mockLogger.Object, _mockMarketData.Object);
        order.Price = Price.FromDecimal(110); // 내 가격
        SimulateOrderActive(order);

        var book = CreateOrderBook(bestBid: 90, bestAsk: 100); // 시장 매도 1호가 100

        // Act
        order.OnMarketDataUpdated(book);

        // Assert
        // 시장가(100)가 나(110)보다 낮으므로, 100 - 1(Tick) = 99로 따라가야 함
        _mockGateway.Verify(g => g.SendReplaceOrderAsync(
            It.Is<ReplaceOrderRequest>(r => r.NewPrice.ToDecimal() == 99m),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Lifecycle & Control Tests

    [Test]
    public void AlgoOrder_Lifecycle_ShouldSubscribeAndUnsubscribe()
    {
        // Arrange
        var order = new OppositeFirstOrder(3001, InstrumentId, Side.Buy, BookName, _mockRouter.Object, _mockGateway.Object, _mockLogger.Object, _mockMarketData.Object);
        order.IsAlgoRunning.Should().BeFalse();

        // Act 1: Activate (Receive New status)
        var reportNew = new OrderStatusReport(order.ClientOrderId, "ExId_3001", null, InstrumentId, Side.Buy, OrderStatus.New, order.Price, order.Quantity, order.Quantity, 0);
        order.OnStatusReportReceived(reportNew);

        // Assert 1: Should be Active and Subscribed
        order.IsAlgoRunning.Should().BeTrue();
        _mockMarketData.Verify(m => m.SubscribeOrderBook(InstrumentId, It.IsAny<string>(), It.IsAny<EventHandler<OrderBook>>()), Times.Once);

        // Act 2: Deactivate (Receive Filled status)
        var reportFilled = new OrderStatusReport(order.ClientOrderId, "ExId_3001", null, InstrumentId, Side.Buy, OrderStatus.Filled, order.Price, order.Quantity, Quantity.FromDecimal(0), 0);
        order.OnStatusReportReceived(reportFilled);

        // Assert 2: Should be Inactive and Unsubscribed
        order.IsAlgoRunning.Should().BeFalse();
        _mockMarketData.Verify(m => m.UnsubscribeOrderBook(InstrumentId, It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void AlgoOrder_ShouldNotUpdate_WhenPendingReplace()
    {
        // Arrange
        var order = new OppositeFirstOrder(4001, InstrumentId, Side.Buy, BookName, _mockRouter.Object, _mockGateway.Object, _mockLogger.Object, _mockMarketData.Object);
        order.Price = Price.FromDecimal(90);
        SimulateOrderActive(order);

        // Simulate a Replace request sent (State becomes ReplaceRequest)
        // Order class internal state manipulation
        _mockGateway.Setup(g => g.SendReplaceOrderAsync(It.IsAny<ReplaceOrderRequest>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new OrderModificationResult(true, "ExId_1", null, null));

        // Trigger a replace manually to change status
        order.ReplaceAsync(Price.FromDecimal(91), OrderType.Limit).Wait();

        // Assert state is ReplaceRequest
        order.Status.Should().Be(OrderStatus.ReplaceRequest);

        // Act: Market update comes in suggesting a NEW price (100)
        var book = CreateOrderBook(bestBid: 95, bestAsk: 100);
        order.OnMarketDataUpdated(book);

        // Assert
        // 이미 ReplaceRequest 상태이므로 추가적인 ReplaceAsync 호출은 없어야 함 (최초 1회만 호출됨)
        _mockGateway.Verify(g => g.SendReplaceOrderAsync(It.IsAny<ReplaceOrderRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AlgoOrder_SubmitAsync_ShouldCalculateInitialPrice()
    {
        // Arrange
        _mockMarketData.Setup(m => m.GetOrderBook(InstrumentId))
            .Returns(CreateOrderBook(bestBid: 95, bestAsk: 100)); // BestAsk 100

        var order = new OppositeFirstOrder(5001, InstrumentId, Side.Buy, BookName, _mockRouter.Object, _mockGateway.Object, _mockLogger.Object, _mockMarketData.Object);

        // Act
        await order.SubmitAsync();

        // Assert
        // 1. OppositeFirstBuy -> Ask 1호가 (100)으로 가격이 설정되었는지 확인
        order.Price.ToDecimal().Should().Be(100m);

        // 2. Gateway로 NewOrder가 전송되었는지 확인
        _mockGateway.Verify(g => g.SendNewOrderAsync(
            It.Is<NewOrderRequest>(r => r.Price.ToDecimal() == 100m),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void AlgoOrder_SubmitAsync_ShouldThrow_WhenBookEmpty()
    {
        // Arrange: 빈 오더북 반환
        _mockMarketData.Setup(m => m.GetOrderBook(InstrumentId))
            .Returns((OrderBook)null!);

        var order = new OppositeFirstOrder(5002, InstrumentId, Side.Buy, BookName, _mockRouter.Object, _mockGateway.Object, _mockLogger.Object, _mockMarketData.Object);

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(async () => await order.SubmitAsync());
    }

    #endregion
}

