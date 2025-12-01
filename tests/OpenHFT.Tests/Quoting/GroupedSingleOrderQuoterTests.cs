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

    private GroupedSingleOrderQuoter CreateQuoter(decimal groupingBp = 1.0m)
    {
        return new GroupedSingleOrderQuoter(
            _mockLogger.Object,
            Side.Buy,
            _instrument,
            _mockOrderFactory.Object,
            "TestBook",
            _mockMarketDataManager.Object,
            groupingBp); // [수정] BP 파라미터 전달
    }

    [Test]
    public async Task UpdateQuoteAsync_BuySide_ShouldFloorToDynamicGroupSize()
    {
        // Arrange (Default 1bp)
        var quoter = CreateQuoter(1.0m);

        // 초기 가격 설정: 60,000
        // 1bp = 60,000 * 0.0001 = 6.0
        // Group Multiple = 6.0 / 0.5 (TickSize) = 12 ticks
        // Group Size = 0.5 * 12 = 6.0

        var inputPrice = 60004.5m; // Should floor to 60000.0 (multiples of 6.0)
        var expectedGroupedPrice = 60000.0m;

        var quote = new Quote(Price.FromDecimal(inputPrice), Quantity.FromDecimal(100));

        // Act
        await quoter.UpdateQuoteAsync(quote, false);
        await quoter.UpdateQuoteAsync(quote, false); // for making initial multiple calc stable if needed

        // Assert
        quoter.LatestQuote.Should().NotBeNull();
        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(expectedGroupedPrice);
        _mockOrder.Verify(o => o.SubmitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // [추가] 5bp Grouping 테스트
    [Test]
    public async Task UpdateQuoteAsync_With5BpGrouping_ShouldHaveLargerGroupSize()
    {
        // Arrange: Grouping BP = 5.0
        var quoter = CreateQuoter(5.0m);

        // 기준 가격: 60,000
        // 5bp = 60,000 * 0.0005 = 30.0
        // Group Size should be 30.0

        // Case A: 60029.5 입력 -> 30단위 내림 -> 60000.0 기대
        var inputPriceA = 60029.5m;
        var expectedPriceA = 60000.0m;

        // Act A
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(inputPriceA), Quantity.FromDecimal(100)), false);
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(inputPriceA), Quantity.FromDecimal(100)), false); // Calc init

        // Assert A
        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(expectedPriceA);

        // Case B: 60030.5 입력 -> 30단위 내림 -> 60030.0 기대
        var inputPriceB = 60030.5m;
        var expectedPriceB = 60030.0m;

        // Act B
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(inputPriceB), Quantity.FromDecimal(100)), false);

        // Assert B
        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(expectedPriceB);
    }

    [Test]
    public async Task UpdateQuoteAsync_SellSide_ShouldCeilingToDynamicGroupSize()
    {
        // Arrange (Sell Side requires recreating quoter or overriding helper)
        var quoter = new GroupedSingleOrderQuoter(
            _mockLogger.Object, Side.Sell, _instrument, _mockOrderFactory.Object, "TestBook",
            _mockMarketDataManager.Object, 1.0m);

        // 초기 가격 설정: 60,000 (Group Size = 6.0)
        var inputPrice = 60001.0m; // Should ceiling to 60006.0 (next multiple of 6.0)
        var expectedGroupedPrice = 60006.0m;

        var quote = new Quote(Price.FromDecimal(inputPrice), Quantity.FromDecimal(100));

        // Act
        await quoter.UpdateQuoteAsync(quote, false);
        await quoter.UpdateQuoteAsync(quote, false);

        // Assert
        quoter.LatestQuote.Should().NotBeNull();
        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(expectedGroupedPrice);
    }

    [Test]
    public async Task UpdateQuoteAsync_WhenPriceChangeIsWithinGroup_ShouldNotReplaceOrder()
    {
        // Arrange
        var quoter = CreateQuoter(1.0m);

        // 1. 초기 주문 (60002.0 -> 60000.0 으로 그룹핑, Group Size=6.0)
        var initialQuote = new Quote(Price.FromDecimal(60002.0m), Quantity.FromDecimal(100));
        await quoter.UpdateQuoteAsync(initialQuote, false);
        await quoter.UpdateQuoteAsync(initialQuote, false);

        // Mock Order 상태 동기화
        _mockOrder.SetupGet(o => o.Price).Returns(Price.FromDecimal(60000.0m));

        // 2. 가격 변경 (60004.0 -> 60000.0 으로 그룹핑)
        // 그룹핑된 가격(60000.0)이 기존 주문 가격(60000.0)과 같으므로 정정(Replace)이 일어나지 않아야 함
        var newQuoteSameGroup = new Quote(Price.FromDecimal(60004.0m), Quantity.FromDecimal(100));

        // Act
        await quoter.UpdateQuoteAsync(newQuoteSameGroup, false);

        // Assert
        _mockOrder.Verify(o => o.SubmitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockOrder.Verify(o => o.ReplaceAsync(It.IsAny<Price>(), It.IsAny<OrderType>(), It.IsAny<CancellationToken>()), Times.Never);

        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(60000.0m);
    }

    [Test]
    public async Task UpdateQuoteAsync_WhenPriceChangeCrossesGroup_ShouldReplaceOrder()
    {
        // Arrange
        var quoter = CreateQuoter(1.0m);

        // 1. 초기 주문 (60004.0 -> 60000.0)
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(60004.0m), Quantity.FromDecimal(100)), false);
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(60004.0m), Quantity.FromDecimal(100)), false);
        _mockOrder.SetupGet(o => o.Price).Returns(Price.FromDecimal(60000.0m));

        // 2. 가격 변경 (60007.0 -> 60006.0)
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
        var quoter = CreateQuoter(1.0m);

        // Act 1: First call calculates N based on 60000 (N=12, Group=6.0)
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(60000m), Quantity.FromDecimal(100)), false);
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(60000m), Quantity.FromDecimal(100)), false);

        // Act 2: Second call with huge price drop (e.g., 30000)
        // If recalculated based on 30000 -> 1bp = 3.0. Group Size 3.0.
        // But since N is reused (12), Group Size should still be 6.0.
        // 30004.0 -> Floor(30004 / 6) * 6 = 5000 * 6 = 30000.0
        // If recalculated (size 3): Floor(30004 / 3) * 3 = 10001 * 3 = 30003.0
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(30004.0m), Quantity.FromDecimal(100)), false);

        // Assert
        quoter.LatestQuote!.Value.Price.ToDecimal().Should().Be(30000.0m);
    }

    [Test]
    public async Task UpdateQuoteAsync_WhenPartiallyFilledAndFarFromMid_ShouldCancelOrder()
    {
        // Arrange
        var quoter = CreateQuoter(1.0m);

        // 1. 오더북 데이터 준비 (Mid Price 설정을 위해)
        // Mid Price = 10,000
        var orderBook = new OrderBook(_instrument, null);
        var updates = new PriceLevelEntryArray();
        // Struct indexer usage (if supported) or unsafe/span logic in real code.
        // Here we assume PriceLevelEntryArray works as intended or use direct instantiation if possible.
        // For test simplicity, assuming ApplyEvent logic works or mocking GetMidPrice directly might be safer if OrderBook logic is complex.
        // But following your previous snippet:
        // Note: InlineArray indexer access needs updated compiler/runtime. 
        // If this fails to compile, use Span casting:
        /*
         Span<PriceLevelEntry> span = updates;
         span[0] = ...
         span[1] = ...
        */
        // Assuming Span access for compatibility:
        Span<PriceLevelEntry> updateSpan = updates;
        updateSpan[0] = new PriceLevelEntry(Side.Buy, 9999.5m, 10);
        updateSpan[1] = new PriceLevelEntry(Side.Sell, 10000.5m, 10);

        orderBook.ApplyEvent(new MarketDataEvent(
                sequence: 1,
                timestamp: TimestampUtils.GetTimestampMicros(),
                exchange: _instrument.SourceExchange,
                kind: EventKind.Snapshot,
                instrumentId: _instrument.InstrumentId,
                updates: updates,
                updateCount: 2
        ));

        _mockMarketDataManager.Setup(m => m.GetOrderBook(_instrument.InstrumentId)).Returns(orderBook);

        // 2. 초기 주문 생성 (10,000 가격에 주문)
        var initialPrice = 10000.0m;
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(initialPrice), Quantity.FromDecimal(100)), false);
        await quoter.UpdateQuoteAsync(new Quote(Price.FromDecimal(initialPrice), Quantity.FromDecimal(100)), false);

        // Mock Order 상태 설정
        _mockOrder.SetupGet(o => o.Price).Returns(Price.FromDecimal(initialPrice));
        _mockOrder.SetupGet(o => o.Quantity).Returns(Quantity.FromDecimal(100));
        _mockOrder.SetupGet(o => o.LeavesQuantity).Returns(Quantity.FromDecimal(50)); // 부분 체결됨!

        // 3. 시세 급락 상황 발생 -> 새로운 Quote 9,000
        var newPanicQuote = new Quote(Price.FromDecimal(9000.0m), Quantity.FromDecimal(100));

        // Act
        await quoter.UpdateQuoteAsync(newPanicQuote, false);

        // Assert
        _mockOrder.Verify(o => o.CancelAsync(It.IsAny<CancellationToken>()), Times.Once,
            "부분 체결되었고 가격이 미드에서 멀어졌으므로 취소해야 합니다.");

        _mockOrder.Verify(o => o.ReplaceAsync(It.IsAny<Price>(), It.IsAny<OrderType>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}