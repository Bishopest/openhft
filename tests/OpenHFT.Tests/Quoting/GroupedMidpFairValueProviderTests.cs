using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenHFT.Core.OrderBook;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.FairValue;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Tests.Quoting;

public class GroupedMidpFairValueProviderTests
{
    private OrderBook _mockOrderBook;
    private GroupedMidpFairValueProvider _provider;
    private Instrument _testInstrument;
    private FairValueUpdate? _lastFairValueUpdate;

    [SetUp]
    public void SetUp()
    {
        // Arrange: 테스트에 사용할 Instrument 생성 (TickSize가 중요)
        _testInstrument = new CryptoPerpetual(
                instrumentId: 1001,
                symbol: "BTCUSDT",
                exchange: ExchangeEnum.BINANCE,
                baseCurrency: Currency.BTC,
                quoteCurrency: Currency.USDT,
                tickSize: Price.FromDecimal(0.5m),
                lotSize: Quantity.FromDecimal(0.001m),
                multiplier: 1m,
                minOrderSize: Quantity.FromDecimal(0.001m)
        );
        // OrderBook을 Mocking하여 GetBestBid/GetBestAsk의 반환값을 제어
        _mockOrderBook = new OrderBook(_testInstrument, null);

        // 테스트 대상인 Provider 생성
        _provider = new GroupedMidpFairValueProvider(
            new NullLogger<GroupedMidpFairValueProvider>(),
            _testInstrument.InstrumentId,
            _testInstrument.TickSize
        );

        // FairValueChanged 이벤트를 구독하여 마지막 업데이트를 캡처
        _lastFairValueUpdate = null;
        _provider.FairValueChanged += (sender, update) => _lastFairValueUpdate = update;
    }

    [Test]
    public void Update_WhenGroupingMultipleIsFirstCalculated_ShouldProduceCorrectFairValue()
    {
        // Arrange
        // 1bp = 60000 * 0.0001 = 6.0. TickSize = 0.5. N = 6.0 / 0.5 = 12.
        // Grouping size = 0.5 * 12 = 6.0
        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, 59998.5m, 1m);
        updates[1] = new PriceLevelEntry(Side.Sell, 60001.0m, 1m);

        var marketDataEvent = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            kind: EventKind.Snapshot,
            instrumentId: _testInstrument.InstrumentId,
            exchange: _testInstrument.SourceExchange,
            updateCount: 2,
            updates: updates
        );

        // Act

        var result = _mockOrderBook.ApplyEvent(marketDataEvent);
        var expectedFairValue = Price.FromDecimal((59994.0m + 60006.0m) / 2m); // Expected = 60000.0

        // Act
        _provider.Update(_mockOrderBook);

        // Assert
        _lastFairValueUpdate.Should().NotBeNull();
        _lastFairValueUpdate.Value.FairValue.Should().Be(expectedFairValue);
        _lastFairValueUpdate.Value.InstrumentId.Should().Be(_testInstrument.InstrumentId);
    }

    [Test]
    public void Update_WithPriceChangesWithinGroup_ShouldNotFireEvent()
    {
        // Arrange
        // 1. 첫 번째 업데이트로 그룹핑 단위(N=12, Group Size=6.0)를 설정
        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, 59998.5m, 1m);
        updates[1] = new PriceLevelEntry(Side.Sell, 60001.0m, 1m);

        var marketDataEvent = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            kind: EventKind.Snapshot,
            instrumentId: _testInstrument.InstrumentId,
            exchange: _testInstrument.SourceExchange,
            updateCount: 2,
            updates: updates
        );

        var result = _mockOrderBook.ApplyEvent(marketDataEvent);
        _provider.Update(_mockOrderBook);
        _lastFairValueUpdate.Should().NotBeNull();

        // 마지막 업데이트를 리셋
        _lastFairValueUpdate = null;

        // 2. 그룹핑 경계(59994.0, 60006.0)를 넘지 않는 가격으로 변경
        updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, 59990.0m, 1m);
        updates[1] = new PriceLevelEntry(Side.Sell, 60005.5m, 1m);

        marketDataEvent = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            kind: EventKind.Snapshot,
            instrumentId: _testInstrument.InstrumentId,
            exchange: _testInstrument.SourceExchange,
            updateCount: 2,
            updates: updates
        );
        // Act
        _provider.Update(_mockOrderBook);

        // Assert
        // Fair Value가 변경되지 않았으므로 이벤트가 발생해서는 안 됨
        _lastFairValueUpdate.Should().BeNull("because the price change was within the grouping bucket.");
    }

    [Test]
    public void Update_WithPriceChangeCrossingGroupBoundary_ShouldFireEventWithNewFairValue()
    {
        // Arrange
        // 1. 첫 번째 업데이트
        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, 59998.5m, 1m);
        updates[1] = new PriceLevelEntry(Side.Sell, 60001.0m, 1m);

        var marketDataEvent = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            kind: EventKind.Snapshot,
            instrumentId: _testInstrument.InstrumentId,
            exchange: _testInstrument.SourceExchange,
            updateCount: 2,
            updates: updates
        );

        var result = _mockOrderBook.ApplyEvent(marketDataEvent);
        _provider.Update(_mockOrderBook);
        _lastFairValueUpdate = null;

        // 2. 그룹핑 경계를 넘는 가격으로 변경
        updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, 59993.5m, 1m);
        updates[1] = new PriceLevelEntry(Side.Sell, 60006.5m, 1m);

        marketDataEvent = new MarketDataEvent(
           sequence: 1,
           timestamp: TimestampUtils.GetTimestampMicros(),
           kind: EventKind.Snapshot,
           instrumentId: _testInstrument.InstrumentId,
           exchange: _testInstrument.SourceExchange,
           updateCount: 2,
           updates: updates
       );

        result = _mockOrderBook.ApplyEvent(marketDataEvent);
        _provider.Update(_mockOrderBook);

        var expectedNewFairValue = Price.FromDecimal((59988.0m + 60012.0m) / 2m); // Expected = 60000.0. Oh, still the same.
        // Let's make a bigger jump.
        updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, 59987.5m, 1m);
        updates[1] = new PriceLevelEntry(Side.Sell, 60006.5m, 1m);

        marketDataEvent = new MarketDataEvent(
           sequence: 1,
           timestamp: TimestampUtils.GetTimestampMicros(),
           kind: EventKind.Snapshot,
           instrumentId: _testInstrument.InstrumentId,
           exchange: _testInstrument.SourceExchange,
           updateCount: 2,
           updates: updates
       );

        result = _mockOrderBook.ApplyEvent(marketDataEvent);
        _provider.Update(_mockOrderBook);
        expectedNewFairValue = Price.FromDecimal((59982.0m + 60012.0m) / 2m); // Expected = 59997.0

        // Act
        _provider.Update(_mockOrderBook);

        // Assert
        _lastFairValueUpdate.Should().NotBeNull();
        _lastFairValueUpdate.Value.FairValue.Should().Be(expectedNewFairValue);
    }

    [Test]
    public void Update_WhenOneSideOfBookIsEmpty_ShouldReturnNullAndNotFireEvent()
    {
        // Arrange
        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, 0m, 0m);
        updates[1] = new PriceLevelEntry(Side.Sell, 60000m, 1m);

        var marketDataEvent = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            kind: EventKind.Snapshot,
            instrumentId: _testInstrument.InstrumentId,
            exchange: _testInstrument.SourceExchange,
            updateCount: 2,
            updates: updates
        );

        var result = _mockOrderBook.ApplyEvent(marketDataEvent);
        _provider.Update(_mockOrderBook);

        // Act
        _provider.Update(_mockOrderBook);

        // Assert
        _lastFairValueUpdate.Should().BeNull();
    }

    [Test]
    public void CalculateGroupingMultiple_When1bpIsSmallerThanTickSize_ShouldReturnOne()
    {
        // Arrange
        // 1bp = 100 * 0.0001 = 0.01. TickSize = 0.5. 1bp < TickSize.
        var provider = new GroupedMidpFairValueProvider(new NullLogger<GroupedMidpFairValueProvider>(), _testInstrument.InstrumentId, _testInstrument.TickSize);
        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, 99.5m, 1m);
        updates[1] = new PriceLevelEntry(Side.Sell, 100.5m, 1m);

        var marketDataEvent = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            kind: EventKind.Snapshot,
            instrumentId: _testInstrument.InstrumentId,
            exchange: _testInstrument.SourceExchange,
            updateCount: 2,
            updates: updates
        );

        var result = _mockOrderBook.ApplyEvent(marketDataEvent);

        // Act & Assert
        // We need to test the private method. We can use reflection or make it internal.
        // For simplicity, let's test its effect via the public method.
        provider.FairValueChanged += (s, u) => _lastFairValueUpdate = u;
        _provider.Update(_mockOrderBook);

        // Grouped Bid = floor(99.5 / 0.5) * 0.5 = 199 * 0.5 = 99.5
        // Grouped Ask = ceil(100.5 / 0.5) * 0.5 = 201 * 0.5 = 100.5
        // Expected Mid = (99.5 + 100.5) / 2 = 100.0
        _lastFairValueUpdate.Value.FairValue.Should().Be(Price.FromDecimal(100.0m));
    }
}