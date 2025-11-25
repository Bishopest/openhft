using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using FluentAssertions;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Hedging;
using OpenHFT.Quoting.Models;
using OpenHFT.Processing;
using OpenHFT.Core.Utils;

namespace OpenHFT.Tests.Hedging;

[TestFixture]
public class HedgerTests
{
    private Mock<ILogger<Hedger>> _mockLogger;
    private Mock<IOrderFactory> _mockOrderFactory;
    private Mock<IMarketDataManager> _mockMarketDataManager;
    private Mock<IOrder> _mockOrder;

    // Test Instruments
    private CryptoPerpetual _linearInstrument; // BTCUSDT
    private CryptoPerpetual _inverseInstrument; // XBTUSD

    // Callbacks to simulate market data updates
    private Action<object, OrderBook> _hedgeBookCallback;
    private Action<object, OrderBook> _refBookCallback;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<Hedger>>();
        _mockOrderFactory = new Mock<IOrderFactory>();
        _mockMarketDataManager = new Mock<IMarketDataManager>();
        _mockOrder = new Mock<IOrder>();
        _mockOrderFactory.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<Side>(), It.IsAny<string>()))
                         .Returns(_mockOrder.Object);

        // Capture subscriptions to trigger them later
        _mockMarketDataManager.Setup(m => m.SubscribeOrderBook(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<EventHandler<OrderBook>>()))
            .Callback<int, string, EventHandler<OrderBook>>((id, key, cb) =>
            {
                if (key.Contains("HedgerRef")) _refBookCallback = (s, b) => cb(s, b);
                else _hedgeBookCallback = (s, b) => cb(s, b);
            });

        // --- Initialize Instruments ---
        // 1. Linear (BTCUSDT): Base=BTC, Denom=USDT. Value = P * Q
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

        // 2. Inverse (XBTUSD): Base=BTC, Denom=BTC. Value = Q / P (Assuming contract size 1 USD)
        // Note: Hedger logic uses ValueInDenominationCurrency(price, 1) to calculate unit value.
        // For Inverse, 1 contract worth in BTC = 1 / Price.
        _inverseInstrument = new CryptoPerpetual(
            instrumentId: 1,
            symbol: "XBTUSD",
            exchange: ExchangeEnum.BITMEX,
            baseCurrency: Currency.BTC,
            quoteCurrency: Currency.USD,
            tickSize: Price.FromDecimal(0.1m),
            lotSize: Quantity.FromDecimal(100m),
            multiplier: 1m,
            minOrderSize: Quantity.FromDecimal(100m)
        );
    }

    private Hedger CreateHedger(Instrument quote, Instrument hedge, int? refId = null)
    {
        var param = new HedgingParameters(hedge.InstrumentId, HedgeOrderType.OppositeFirst, Quantity.FromDecimal(1000m));
        return new Hedger(
            _mockLogger.Object, quote, hedge, param,
            _mockOrderFactory.Object, "HedgeBook",
            _mockMarketDataManager.Object, refId
        );
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

    private IOrder? GetPendingOrder(Hedger hedger)
    {
        var field = typeof(Hedger).GetField("_hedgeOrder", BindingFlags.NonPublic | BindingFlags.Instance);
        return (IOrder?)field.GetValue(hedger);
    }

    // ==================================================================================
    // Scenario 1: Inverse Quote (XBTUSD) -> Inverse Hedge (XBTUSD)
    // ==================================================================================
    [Test]
    public void Scenario_InverseToInverse_NoConversion()
    {
        var hedger = CreateHedger(_inverseInstrument, _inverseInstrument);
        hedger.Activate();

        var hedgeBook = new OrderBook(_inverseInstrument, null);
        var hedgeMidPrice = 50000m;
        // BestBid: 49999.75, BestAsk: 50000.25
        hedgeBook.ApplyEvent(CreateSnapshot(_inverseInstrument.SourceExchange, _inverseInstrument.InstrumentId, hedgeMidPrice - 0.25m, hedgeMidPrice + 0.25m));
        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);

        // Act: Buy 100 contracts
        var fill = new Fill(_inverseInstrument.InstrumentId, "Test", 1, "1", "1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(100), 0);
        hedger.UpdateQuotingFill(fill);

        // Assert Logic
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
        ForceActivate(hedger);

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
        int refId = _linearInstrument.InstrumentId;
        var hedger = CreateHedger(_linearInstrument, _inverseInstrument, refId);

        hedger.Activate();
        ForceActivate(hedger);

        // Ref Price (BTC/USDT) = 50,000
        var quoteBook = new OrderBook(_linearInstrument, null);
        quoteBook.ApplyEvent(CreateSnapshot(_linearInstrument.SourceExchange, _linearInstrument.InstrumentId, 49999.75m, 50000.25m)); // Mid ~50000

        // Hedge Price (XBT/USD) = 50,000
        var hedgeBook = new OrderBook(_inverseInstrument, null);
        hedgeBook.ApplyEvent(CreateSnapshot(_inverseInstrument.SourceExchange, _inverseInstrument.InstrumentId, 49999.75m, 50000.25m));

        // Callbacks to update hedger state
        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);
        if (_refBookCallback != null) _refBookCallback(this, quoteBook);

        // Act: Buy 1 BTC on Linear @ 50,000
        // Value = 50,000 USDT -> Needs -50,000 USDT worth of XBT
        var fill = new Fill(_linearInstrument.InstrumentId, "Test", 1, "1", "1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(1), 0);
        hedger.UpdateQuotingFill(fill);

        // Assert
        // Target: 50,000 USDT / 50,000 (Ref) = 1 BTC.
        // Inverse Qty: 1 BTC / (1/50000) = 50,000 Contracts.
        var pendingQty = GetPendingQuantity(hedger);
        pendingQty.Should().Be(-50000m);
    }

    // ==================================================================================
    // Scenario 4: Inverse (XBTUSD) -> Linear (BTCUSDT) [BTC -> USDT]
    // ==================================================================================
    [Test]
    public void Scenario_InverseToLinear_CrossCurrency()
    {
        int refId = _linearInstrument.InstrumentId;
        var hedger = CreateHedger(_inverseInstrument, _linearInstrument, refId);

        hedger.Activate();
        ForceActivate(hedger);

        var quoteBook = new OrderBook(_inverseInstrument, null);
        var hedgeBook = new OrderBook(_linearInstrument, null);

        // Both prices ~50,000
        quoteBook.ApplyEvent(CreateSnapshot(_inverseInstrument.SourceExchange, _inverseInstrument.InstrumentId, 49999.75m, 50000.25m));
        hedgeBook.ApplyEvent(CreateSnapshot(_linearInstrument.SourceExchange, _linearInstrument.InstrumentId, 49999.75m, 50000.25m));

        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);
        if (_refBookCallback != null) _refBookCallback(this, hedgeBook); // Using hedge book as ref since it is Linear

        // Act: Buy 50,000 Contracts Inverse
        // Value = 1 BTC -> Needs -1 BTC worth of USDT
        var fill = new Fill(_inverseInstrument.InstrumentId, "Test", 1, "1", "1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(50000), 0);
        hedger.UpdateQuotingFill(fill);

        // Assert
        // Target: 1 BTC * 50,000 (Ref) = 50,000 USDT.
        // Linear Qty: 50,000 / 50,000 (UnitVal) = 1 BTC.
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
        var param = new HedgingParameters(_linearInstrument.InstrumentId, HedgeOrderType.OppositeFirst, Quantity.FromDecimal(0.5m));
        var hedger = new Hedger(
            _mockLogger.Object, _linearInstrument, _linearInstrument, param,
            _mockOrderFactory.Object, "HedgeBook", _mockMarketDataManager.Object
        );
        hedger.Activate();
        ForceActivate(hedger);

        var hedgeBook = new OrderBook(_linearInstrument, null);
        hedgeBook.ApplyEvent(CreateSnapshot(_linearInstrument.SourceExchange, _linearInstrument.InstrumentId, 49999.75m, 50000.25m));
        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);

        // Act: Buy 0.7 BTC
        var fill = new Fill(_linearInstrument.InstrumentId, "Test", 1, "1", "1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(0.7m), 0);
        hedger.UpdateQuotingFill(fill);

        // Assert 1: First Slice
        // Pending: -0.7
        GetPendingQuantity(hedger).Should().Be(-0.7m);
        // Simulate Fill of the first hedge order
        var hedgeFill = new Fill(_linearInstrument.InstrumentId, "Hedge", 123, "Ex1", "Ex1", Side.Sell, Price.FromDecimal(50000), Quantity.FromDecimal(0.5m), 0);

        // Reset order state for next verification
        if (_hedgeBookCallback != null) _hedgeBookCallback(this, hedgeBook);

        // Trigger Hedging Fill -> Should trigger next slice
        hedger.OnHedgingFill(this, hedgeFill);

        // Assert 2: Second Slice
        // Pending: -0.7 + 0.5 = -0.2
        GetPendingQuantity(hedger).Should().Be(-0.2m);
    }
}