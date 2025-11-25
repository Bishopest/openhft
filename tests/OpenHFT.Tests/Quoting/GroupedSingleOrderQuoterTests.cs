// ---- FILE: Tests/Quoting/GroupedSingleOrderQuoterTests.cs ----

using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using FluentAssertions;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Quoting;
using System.Threading;
using System.Threading.Tasks;
using OpenHFT.Quoting.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Tests.Quoting;

[TestFixture]
public class GroupedSingleOrderQuoterTests
{
    private Mock<ILogger> _mockLogger;
    private Mock<IOrderFactory> _mockOrderFactory;
    private Mock<IMarketDataManager> _mockMarketDataManager;
    private Mock<IOrder> _mockOrder;
    private Instrument _instrument;

    // 테스트 설정: TickSize = 0.5
    // 1bp = 0.01%
    private const decimal TickSize = 0.5m;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger>();
        _mockOrderFactory = new Mock<IOrderFactory>();
        _mockMarketDataManager = new Mock<IMarketDataManager>();
        _mockOrder = new Mock<IOrder>();

        // XBTUSD 스타일의 상품 생성
        _instrument = new CryptoPerpetual(
            1, "XBTUSD", ExchangeEnum.BITMEX,
            Currency.FromString("XBT"), Currency.FromString("USD"),
            Price.FromDecimal(TickSize), Quantity.FromDecimal(1), 1m, Quantity.FromDecimal(1));

        // Mock Order 설정
        _mockOrder.SetupAllProperties();
        _mockOrder.SetupGet(o => o.ClientOrderId).Returns(12345);
        _mockOrder.SetupGet(o => o.Price).Returns(Price.FromDecimal(100));
        _mockOrder.SetupGet(o => o.Quantity).Returns(Quantity.FromDecimal(1));
        _mockOrder.SetupGet(o => o.LeavesQuantity).Returns(Quantity.FromDecimal(1));
        _mockOrder.Setup(o => o.SubmitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockOrder.Setup(o => o.ReplaceAsync(It.IsAny<Price>(), It.IsAny<OrderType>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Factory가 Mock Order를 반환하도록 설정
        _mockOrderFactory.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<Side>(), It.IsAny<string>()))
                         .Returns(_mockOrder.Object);
        _mockMarketDataManager.Setup(m => m.GetOrderBook(It.IsAny<int>())).Returns((OrderBook?)null);
    }

    [Test]
    public async Task UpdateQuoteAsync_BuySide_ShouldFloorToDynamicGroupSize()
    {
        // Arrange
        var quoter = new GroupedSingleOrderQuoter(
            _mockLogger.Object, Side.Buy, _instrument, _mockOrderFactory.Object, "TestBook", _mockMarketDataManager.Object);

        // 초기 가격 설정: 60,000
        // 1bp = 60,000 * 0.0001 = 6.0
        // Group Multiple = 6.0 / 0.5 (TickSize) = 12 ticks
        // Group Size = 0.5 * 12 = 6.0

        var inputPrice = 60004.5m; // Should floor to 60000.0 (multiples of 6.0)
        var expectedGroupedPrice = 60000.0m;

        var quote = new Quote(Price.FromDecimal(inputPrice), Quantity.FromDecimal(100));

        // Act
        // for making initial multiple
        await quoter.UpdateQuoteAsync(quote, false);
        await quoter.UpdateQuoteAsync(quote, false);

        // Assert
        // 1. 그룹핑된 가격으로 주문이 생성되었는지 확인 (Mock Order의 Price 속성 검증은 OrderBuilder가 설정한다고 가정할 때 간접적으로 확인)
        // 하지만 여기서는 LatestQuote 속성을 통해 확인하는 것이 가장 확실함
        quoter.LatestQuote.Should().NotBeNull();
        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(expectedGroupedPrice);

        // 2. SubmitAsync 호출 확인
        _mockOrder.Verify(o => o.SubmitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task UpdateQuoteAsync_SellSide_ShouldCeilingToDynamicGroupSize()
    {
        // Arrange
        var quoter = new GroupedSingleOrderQuoter(
            _mockLogger.Object, Side.Sell, _instrument, _mockOrderFactory.Object, "TestBook", _mockMarketDataManager.Object);

        // 초기 가격 설정: 60,000 (Group Size = 6.0)
        var inputPrice = 60001.0m; // Should ceiling to 60006.0 (next multiple of 6.0)
        var expectedGroupedPrice = 60006.0m;

        var quote = new Quote(Price.FromDecimal(inputPrice), Quantity.FromDecimal(100));

        // Act
        // for making initial multiple
        await quoter.UpdateQuoteAsync(quote, false);
        await quoter.UpdateQuoteAsync(quote, false);

        // Assert
        quoter.LatestQuote.Should().NotBeNull();
        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(expectedGroupedPrice);

        _mockOrder.Verify(o => o.SubmitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task UpdateQuoteAsync_WhenPriceChangeIsWithinGroup_ShouldNotReplaceOrder()
    {
        // Arrange
        var quoter = new GroupedSingleOrderQuoter(
            _mockLogger.Object, Side.Buy, _instrument, _mockOrderFactory.Object, "TestBook", _mockMarketDataManager.Object);

        // 1. 초기 주문 (60002.0 -> 60000.0 으로 그룹핑, Group Size=6.0)
        var initialQuote = new Quote(Price.FromDecimal(60002.0m), Quantity.FromDecimal(100));
        // for making intial multiple
        await quoter.UpdateQuoteAsync(initialQuote, false);
        await quoter.UpdateQuoteAsync(initialQuote, false);

        // Mock Order가 현재 가격을 60000.0으로 가지고 있다고 설정 (실제 로직에서는 OrderBuilder가 설정했음)
        _mockOrder.SetupGet(o => o.Price).Returns(Price.FromDecimal(60000.0m));

        // 2. 가격 변경 (60004.0 -> 60000.0 으로 그룹핑)
        // 그룹핑된 가격(60000.0)이 기존 주문 가격(60000.0)과 같으므로 정정(Replace)이 일어나지 않아야 함
        var newQuoteSameGroup = new Quote(Price.FromDecimal(60004.0m), Quantity.FromDecimal(100));

        // Act
        await quoter.UpdateQuoteAsync(newQuoteSameGroup, false);

        // Assert
        // Submit은 초기 1번만 호출됨
        _mockOrder.Verify(o => o.SubmitAsync(It.IsAny<CancellationToken>()), Times.Once);
        // Replace는 호출되지 않아야 함
        _mockOrder.Verify(o => o.ReplaceAsync(It.IsAny<Price>(), It.IsAny<OrderType>(), It.IsAny<CancellationToken>()), Times.Never);

        // LatestQuote는 업데이트된 그룹핑 가격을 유지해야 함
        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(60000.0m);
    }

    [Test]
    public async Task UpdateQuoteAsync_WhenPriceChangeCrossesGroup_ShouldReplaceOrder()
    {
        // Arrange
        var quoter = new GroupedSingleOrderQuoter(
            _mockLogger.Object, Side.Buy, _instrument, _mockOrderFactory.Object, "TestBook", _mockMarketDataManager.Object);

        // 1. 초기 주문 (60004.0 -> 60000.0)
        // for making initial multiple
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(60004.0m), Quantity.FromDecimal(100)), false);
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(60004.0m), Quantity.FromDecimal(100)), false);
        _mockOrder.SetupGet(o => o.Price).Returns(Price.FromDecimal(60000.0m));

        // 2. 가격 변경 (60007.0 -> 60006.0)
        // 그룹핑된 가격이 60006.0으로 변경되었으므로 정정해야 함
        var newQuoteDiffGroup = new Quote(Price.FromDecimal(60007.0m), Quantity.FromDecimal(100));
        var expectedGroupedPrice = 60006.0m;

        // Act
        await quoter.UpdateQuoteAsync(newQuoteDiffGroup, false);

        // Assert
        _mockOrder.Verify(o => o.ReplaceAsync(
            It.Is<Price>(p => p.ToDecimal() == expectedGroupedPrice),
            OrderType.Limit,
            It.IsAny<CancellationToken>()),
            Times.Once);

        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(expectedGroupedPrice);
    }

    [Test]
    public async Task CalculateGroupingMultiple_ShouldBeCalculatedOnceAndReused()
    {
        // Arrange
        var quoter = new GroupedSingleOrderQuoter(
            _mockLogger.Object, Side.Buy, _instrument, _mockOrderFactory.Object, "TestBook", _mockMarketDataManager.Object);

        // Act 1: First call calculates N based on 60000 (N=12, Group=6.0)
        // for making initial multiple
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(60000m), Quantity.FromDecimal(100)), false);
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(60000m), Quantity.FromDecimal(100)), false);
        var firstGroupedPrice = quoter.LatestQuote!.Value.Price.ToDecimal(); // 60000.0

        // Act 2: Second call with huge price drop (e.g., 30000)
        // If N was recalculated, 1bp of 30000 = 3.0. N would be 6. Group Size 3.0.
        // But since N is reused (12), Group Size should still be 6.0.
        // 30004.0 -> Floor(30004 / 6) * 6 = 5000 * 6 = 30000.0
        // If recalculated (size 3): Floor(30004 / 3) * 3 = 10001 * 3 = 30003.0
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(30004.0m), Quantity.FromDecimal(100)), false);

        // Assert
        // Reuse Logic Check: 30004 should group to 30000 (multiple 6), not 30003 (multiple 3)
        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(30000.0m);
    }

    [Test]
    public async Task UpdateQuoteAsync_WhenPartiallyFilledAndFarFromMid_ShouldCancelOrder()
    {
        // Arrange
        var quoter = new GroupedSingleOrderQuoter(
            _mockLogger.Object, Side.Buy, _instrument, _mockOrderFactory.Object, "TestBook", _mockMarketDataManager.Object);

        // 1. 오더북 데이터 준비 (Mid Price 설정을 위해)
        // Mid Price = 10,000 으로 설정
        // 3bp 범위: 10,000 * 0.0003 = 3. 
        // Safe Range: 9,997 ~ 10,003
        var orderBook = new OrderBook(_instrument, null);
        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, 9999.5m, 10);
        updates[1] = new PriceLevelEntry(Side.Sell, 10000.5m, 10);
        orderBook.ApplyEvent(new MarketDataEvent(
                sequence: 1,
                timestamp: TimestampUtils.GetTimestampMicros(),
                exchange: _instrument.SourceExchange,
                kind: EventKind.Snapshot,
                instrumentId: _instrument.InstrumentId,
                updates: updates,
                updateCount: 2
        ));

        // MarketDataManager가 위 오더북을 반환하도록 설정
        _mockMarketDataManager.Setup(m => m.GetOrderBook(_instrument.InstrumentId)).Returns(orderBook);

        // 2. 초기 주문 생성 (10,000 가격에 주문)
        var initialPrice = 10000.0m;
        // for making initial multiple (10000 * 0.0001 = 1.0. Tick 0.5. Multiple=2. GroupSize=1.0)
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(initialPrice), Quantity.FromDecimal(100)), false);
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(initialPrice), Quantity.FromDecimal(100)), false);

        // Mock Order 상태 설정: 현재가 10000, 부분 체결 상태 (Qty 100, Leaves 50)
        _mockOrder.SetupGet(o => o.Price).Returns(Price.FromDecimal(initialPrice));
        _mockOrder.SetupGet(o => o.Quantity).Returns(Quantity.FromDecimal(100));
        _mockOrder.SetupGet(o => o.LeavesQuantity).Returns(Quantity.FromDecimal(50)); // 부분 체결됨!

        // 3. 시세 급락 상황 발생 -> 새로운 Quote가 9,000원으로 들어옴 (Mid 10,000에서 아주 멂)
        // 9,000은 Safe Range (9,997 ~ 10,003) 밖임 -> Cancel 대상
        var newPanicQuote = new Quote(Price.FromDecimal(9000.0m), Quantity.FromDecimal(100));

        // Act
        await quoter.UpdateQuoteAsync(newPanicQuote, false);

        // Assert
        // 조건: PartiallyFilled(True) && !IsNearMid(True because 9000 is far from 10000)
        // 기대: CancelAsync 호출, ReplaceAsync 호출 안 함

        _mockOrder.Verify(o => o.CancelAsync(It.IsAny<CancellationToken>()), Times.Once,
            "부분 체결되었고 가격이 미드에서 멀어졌으므로 취소해야 합니다.");

        _mockOrder.Verify(o => o.ReplaceAsync(It.IsAny<Price>(), It.IsAny<OrderType>(), It.IsAny<CancellationToken>()), Times.Never,
            "취소해야 할 상황에서 정정 주문을 내면 안 됩니다.");
    }
}