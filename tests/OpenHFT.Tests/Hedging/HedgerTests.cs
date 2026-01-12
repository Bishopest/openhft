using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using FluentAssertions;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Hedging;
using OpenHFT.Core.Utils;
using OpenHFT.Core.Orders;

namespace OpenHFT.Tests.Hedging;

[TestFixture]
public class HedgerTests
{
    private Mock<ILogger<Hedger>> _mockLogger;
    private Mock<IOrderFactory> _mockOrderFactory;
    private Mock<IMarketDataManager> _mockMarketDataManager;
    private Mock<IFxRateService> _mockFxRateService;
    private Mock<IOrder> _mockOrder; // Mock 객체로 복귀

    // Test Instruments
    private CryptoPerpetual _linearInstrument; // BTCUSDT
    private CryptoPerpetual _inverseInstrument; // XBTUSD

    private Action<object, OrderBook> _hedgeBookCallback;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<Hedger>>();
        _mockOrderFactory = new Mock<IOrderFactory>();
        _mockMarketDataManager = new Mock<IMarketDataManager>();
        _mockFxRateService = new Mock<IFxRateService>();
        _mockOrder = new Mock<IOrder>();
        _mockOrder.SetupAllProperties();

        // OrderFactory가 Mock Order를 반환하도록 설정
        _mockOrderFactory.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<Side>(), It.IsAny<string>(), It.IsAny<OrderSource>(), It.IsAny<AlgoOrderType>()))
                         .Returns(_mockOrder.Object);

        // Capture subscriptions
        _mockMarketDataManager.Setup(m => m.SubscribeOrderBook(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<EventHandler<OrderBook>>()))
            .Callback<int, string, EventHandler<OrderBook>>((id, key, cb) =>
            {
                _hedgeBookCallback = (s, b) => cb(s, b);
            });

        // --- Initialize Instruments ---
        _linearInstrument = new CryptoPerpetual(
                instrumentId: 1001,
                symbol: "BTCUSDT",
                exchange: ExchangeEnum.BINANCE,
                baseCurrency: Currency.BTC,
                quoteCurrency: Currency.USDT,
                tickSize: Price.FromDecimal(0.1m),
                lotSize: Quantity.FromDecimal(0.001m),
                multiplier: 1m,
                minOrderSize: Quantity.FromDecimal(0.001m)
        );

        _inverseInstrument = new CryptoPerpetual(
            instrumentId: 1,
            symbol: "XBTUSD",
            exchange: ExchangeEnum.BITMEX,
            baseCurrency: Currency.BTC,
            quoteCurrency: Currency.USD,
            tickSize: Price.FromDecimal(0.5m),
            lotSize: Quantity.FromDecimal(100m),
            multiplier: 1m,
            minOrderSize: Quantity.FromDecimal(100m)
        );
    }

    private Hedger CreateHedger(Instrument quote, Instrument hedge, int? refId = null)
    {
        var param = new HedgingParameters(quote.InstrumentId, hedge.InstrumentId, AlgoOrderType.OppositeFirst, Quantity.FromDecimal(1000m));
        return new Hedger(
            _mockLogger.Object, quote, hedge, param,
            _mockOrderFactory.Object, "HedgeBook",
            _mockMarketDataManager.Object, _mockFxRateService.Object
        );
    }

    private void SetupFxRate(decimal rate, Currency target)
    {
        // rate: BTC/USDT price (50000)
        // Convert(amount, target) Logic simulation
        _mockFxRateService.Setup(s => s.Convert(It.IsAny<CurrencyAmount>(), target))
            .Returns<CurrencyAmount, Currency>((source, tgt) =>
            {
                if (source.Currency == tgt) return source;

                decimal converted = 0m;
                // BTC -> USDT (Multiply)
                if (source.Currency == Currency.BTC && tgt == Currency.USDT)
                    converted = source.Amount * rate;
                // USDT -> BTC (Divide)
                else if (source.Currency == Currency.USDT && tgt == Currency.BTC)
                    converted = source.Amount / rate;

                return new CurrencyAmount(converted, tgt);
            });
    }

    // Helper to force Activate state via Reflection because Activate() has strict checks
    private void ForceActivate(Hedger hedger)
    {
        var field = typeof(Hedger).GetField("IsActive", BindingFlags.NonPublic | BindingFlags.Instance);
        field.SetValue(hedger, true);
    }

    private MarketDataEvent CreateSnapshot(ExchangeEnum exchange, int instrumentId, decimal bestBid, decimal bestAsk)
    {
        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, bestBid, 10);
        updates[1] = new PriceLevelEntry(Side.Sell, bestAsk, 10);
        return new MarketDataEvent(
                sequence: 1,
                timestamp: TimestampUtils.GetTimestampMicros(),
                exchange: exchange,
                kind: EventKind.Snapshot,
                instrumentId: instrumentId,
                updates: updates,
                updateCount: 2
        );
    }

    private decimal GetPendingQuantity(Hedger hedger)
    {
        var field = typeof(Hedger).GetField("_netPendingHedgeQuantity", BindingFlags.NonPublic | BindingFlags.Instance);
        var qty = (Quantity)field.GetValue(hedger);
        return qty.ToDecimal();
    }

    // ==================================================================================
    // Scenario 1: Inverse Quote (XBTUSD) -> Inverse Hedge (XBTUSD)
    // ==================================================================================
    [Test]
    public void Scenario_InverseToInverse_NoConversion()
    {
        var hedger = CreateHedger(_inverseInstrument, _inverseInstrument);
        hedger.Activate();

        SetupFxRate(1m, Currency.BTC);

        var hedgeBook = new OrderBook(_inverseInstrument, null);
        var hedgeMidPrice = 50000m;
        hedgeBook.ApplyEvent(CreateSnapshot(_inverseInstrument.SourceExchange, _inverseInstrument.InstrumentId, hedgeMidPrice - 0.25m, hedgeMidPrice + 0.25m));
        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);

        // Act: Buy 100 contracts
        var fill = new Fill(_inverseInstrument.InstrumentId, "Test", 1, "1", "1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(100), 0);
        hedger.UpdateQuotingFill(fill);

        // Assert Logic: 내부 계산 결과 확인
        var pendingQty = GetPendingQuantity(hedger);
        pendingQty.Should().Be(-100m);
    }

    // ==================================================================================
    // Scenario 2: Linear Quote (BTCUSDT) -> Linear Hedge (BTCUSDT)
    // ==================================================================================
    [Test]
    public void Scenario_LinearToLinear_NoConversion()
    {
        var hedger = CreateHedger(_linearInstrument, _linearInstrument);
        hedger.Activate();

        SetupFxRate(1m, Currency.USDT);

        var hedgeBook = new OrderBook(_linearInstrument, null);
        var hedgeMidPrice = 50000m;
        hedgeBook.ApplyEvent(CreateSnapshot(_linearInstrument.SourceExchange, _linearInstrument.InstrumentId, hedgeMidPrice - 0.25m, hedgeMidPrice + 0.25m));
        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);

        // Act: Buy 1.001 BTC
        var fill = new Fill(_linearInstrument.InstrumentId, "Test", 1, "1", "1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(1.001m), 0);
        hedger.UpdateQuotingFill(fill);

        // Assert
        var pendingQty = GetPendingQuantity(hedger);
        pendingQty.Should().Be(-1.001m);
    }

    // ==================================================================================
    // Scenario 3: Linear (BTCUSDT) -> Inverse (XBTUSD) [USDT -> BTC]
    // ==================================================================================
    [Test]
    public void Scenario_LinearToInverse_CrossCurrency()
    {
        var hedger = CreateHedger(_linearInstrument, _inverseInstrument);
        hedger.Activate();

        // Mock FX Rate (BTC/USDT = 50000)
        SetupFxRate(50000m, Currency.BTC);

        var hedgeBook = new OrderBook(_inverseInstrument, null);
        hedgeBook.ApplyEvent(CreateSnapshot(_inverseInstrument.SourceExchange, _inverseInstrument.InstrumentId, 49999.75m, 50000.25m));
        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);

        // Act: Buy 1 BTC on Linear @ 50,000
        // Value = 50,000 USDT -> Needs -50,000 USDT worth of XBT
        var fill = new Fill(_linearInstrument.InstrumentId, "Test", 1, "1", "1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(1), 0);
        hedger.UpdateQuotingFill(fill);

        // Assert
        // FxService converts 50,000 USDT -> 1 BTC
        // Inverse Qty: 1 BTC / (1/50000) = 50,000 Contracts
        var pendingQty = GetPendingQuantity(hedger);
        pendingQty.Should().Be(-50000m);
    }

    // ==================================================================================
    // Scenario 4: Inverse (XBTUSD) -> Linear (BTCUSDT) [BTC -> USDT]
    // ==================================================================================
    [Test]
    public void Scenario_InverseToLinear_CrossCurrency()
    {
        var hedger = CreateHedger(_inverseInstrument, _linearInstrument);
        hedger.Activate();

        // Mock FX Rate (BTC/USDT = 50000)
        SetupFxRate(50000m, Currency.USDT);

        var hedgeBook = new OrderBook(_linearInstrument, null);
        hedgeBook.ApplyEvent(CreateSnapshot(_linearInstrument.SourceExchange, _linearInstrument.InstrumentId, 49999.75m, 50000.25m));
        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);

        // Act: Buy 50,000 Contracts Inverse
        // Value = 1 BTC -> Needs -1 BTC worth of USDT
        var fill = new Fill(_inverseInstrument.InstrumentId, "Test", 1, "1", "1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(50000), 0);
        hedger.UpdateQuotingFill(fill);

        // Assert
        // FxService converts 1 BTC -> 50,000 USDT
        // Linear Qty: 50,000 / 50,000 (UnitVal) = 1 BTC
        var pendingQty = GetPendingQuantity(hedger);
        pendingQty.Should().Be(-1m);
    }

    // ==================================================================================
    // Scenario 5: Slicing / Max Order Size Logic
    // ==================================================================================
    [Test]
    public void Scenario_MaxOrderSize_Slicing()
    {
        // 1. Arrange: Max Order Size = 0.5 BTC
        var param = new HedgingParameters(_linearInstrument.InstrumentId, _linearInstrument.InstrumentId, AlgoOrderType.OppositeFirst, Quantity.FromDecimal(0.5m));
        var hedger = new Hedger(
            _mockLogger.Object, _linearInstrument, _linearInstrument, param,
            _mockOrderFactory.Object, "HedgeBook", _mockMarketDataManager.Object,
            _mockFxRateService.Object
        );
        hedger.Activate();
        SetupFxRate(1m, Currency.USDT);

        // Mock Order 설정: SubmitAsync가 호출되면 성공한 것으로 간주
        _mockOrder.Setup(o => o.SubmitAsync(It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        // 주문 생성 시 클라이언트 ID 설정 (추적용)
        _mockOrder.Setup(o => o.ClientOrderId).Returns(12345);
        _mockOrder.Setup(o => o.Side).Returns(Side.Sell);
        _mockOrder.Setup(o => o.AlgoOrderType).Returns(AlgoOrderType.OppositeFirst);

        // Hedge OrderBook 설정
        var hedgeBook = new OrderBook(_linearInstrument, null);
        hedgeBook.ApplyEvent(CreateSnapshot(_linearInstrument.SourceExchange, _linearInstrument.InstrumentId, 49999.75m, 50000.25m));
        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);

        // 2. Act: Quoting Fill 발생 (Buy 0.7 BTC)
        // Hedger는 -0.7 BTC 헤지가 필요함을 인지하고, MaxSize인 0.5 BTC 주문을 즉시 제출함.
        var fill = new Fill(_linearInstrument.InstrumentId, "Test", 1, "1", "1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(0.7m), 0);
        hedger.UpdateQuotingFill(fill);

        // 3. Assert 1: 주문 제출 후 상태 확인
        // -0.7(필요) - (-0.5(제출)) = -0.2 (남은 Pending)
        // Hedger는 주문 제출 시점에 이미 수량을 차감하므로, Pending은 -0.2여야 함.
        var pendingAfterSubmit = GetPendingQuantity(hedger);
        pendingAfterSubmit.Should().Be(-0.2m, "First slice (0.5) should be deducted immediately upon submission.");

        // Verify: 실제로 0.5개짜리 주문이 나갔는지 확인
        _mockOrderFactory.Verify(f => f.Create(
            It.IsAny<int>(),
            It.IsAny<Side>(),
            It.IsAny<string>(),
            It.IsAny<OrderSource>(),
            It.IsAny<AlgoOrderType>()), Times.Once);

        // Mock Order의 수량이 0.5로 설정되었는지 확인하는 것은 OrderBuilder 로직에 따라 다르므로 
        // 여기서는 Factory 호출 횟수와 Pending 감소로 검증.

        // 4. Act: 첫 번째 주문 체결 (Filled)
        // 체결이 되면 Hedger는 "주문 종료"를 인식하고, 남은 물량(-0.2)에 대해 추가 주문을 시도함.
        var finalReport = new OrderStatusReport(
            12345, "Ex1", "Exec1", _linearInstrument.InstrumentId, Side.Sell,
            OrderStatus.Filled, Price.FromDecimal(50000), Quantity.FromDecimal(0.5m), Quantity.FromDecimal(0), 0);

        // Hedger의 OnOrderStatusChanged(Filled)를 트리거하기 위해 리플렉션 사용
        // (Hedger 내부에서 OrderBuilder를 통해 이벤트 핸들러를 등록했으므로, 
        //  테스트에서는 OrderBuilder 로직을 타지 않아 이벤트가 연결되지 않았을 수 있음.
        //  따라서 Hedger.ClearActiveOrder를 직접 호출하거나 private 메서드를 invoke 해야 함)

        // 가장 확실한 방법: ClearActiveOrder 메서드를 호출 (Private)
        var clearMethod = typeof(Hedger).GetMethod("ClearActiveOrder", BindingFlags.NonPublic | BindingFlags.Instance);
        clearMethod.Invoke(hedger, new object[] { finalReport });

        // 5. Assert 2: 두 번째 주문 제출 확인
        // 남은 -0.2가 처리되어 Pending은 0이 되어야 함
        var pendingFinal = GetPendingQuantity(hedger);
        pendingFinal.Should().Be(0m, "Remaining slice (0.2) should be deducted upon second submission.");

        // Verify: 두 번째 주문 생성 확인
        _mockOrderFactory.Verify(f => f.Create(
            It.IsAny<int>(),
            It.IsAny<Side>(),
            It.IsAny<string>(),
            It.IsAny<OrderSource>(),
            It.IsAny<AlgoOrderType>()), Times.Exactly(2));
    }
}