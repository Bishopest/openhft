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

        // OrderFactory가 Mock Order를 반환하도록 설정
        _mockOrderFactory.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<Side>(), It.IsAny<string>(), It.IsAny<OrderSource>()))
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
        var param = new HedgingParameters(quote.InstrumentId, hedge.InstrumentId, HedgeOrderType.OppositeFirst, Quantity.FromDecimal(1000m));
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
        // Max Order Size = 0.5 BTC
        var param = new HedgingParameters(_linearInstrument.InstrumentId, _linearInstrument.InstrumentId, HedgeOrderType.OppositeFirst, Quantity.FromDecimal(0.5m));
        var hedger = new Hedger(
            _mockLogger.Object, _linearInstrument, _linearInstrument, param,
            _mockOrderFactory.Object, "HedgeBook", _mockMarketDataManager.Object,
            _mockFxRateService.Object
        );
        hedger.Activate();
        SetupFxRate(1m, Currency.USDT);

        var hedgeBook = new OrderBook(_linearInstrument, null);
        hedgeBook.ApplyEvent(CreateSnapshot(_linearInstrument.SourceExchange, _linearInstrument.InstrumentId, 49999.75m, 50000.25m));
        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);

        // Act: Buy 0.7 BTC
        var fill = new Fill(_linearInstrument.InstrumentId, "Test", 1, "1", "1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(0.7m), 0);
        hedger.UpdateQuotingFill(fill);

        // Assert 1: First Slice (Max 0.5)
        GetPendingQuantity(hedger).Should().Be(-0.7m);

        // Simulate Fill of the first hedge order
        var hedgeFill = new Fill(_linearInstrument.InstrumentId, "Hedge", 123, "Ex1", "Ex1", Side.Sell, Price.FromDecimal(50000), Quantity.FromDecimal(0.5m), 0);

        // Trigger Hedging Fill -> Should trigger next slice
        hedger.OnHedgingFill(this, hedgeFill);

        // Assert 2: Second Slice (Remaining 0.2)
        GetPendingQuantity(hedger).Should().Be(-0.2m);
    }
}