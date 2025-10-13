using NUnit.Framework;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenHFT.Tests.Book;

[TestFixture]
public class OrderBookTests
{
    private ILogger<OrderBook> _logger;
    private string _testDirectory;
    private InstrumentRepository _repository;

    private Instrument _btc;
    private Instrument _eth;
    [SetUp]
    public void Setup()
    {
        var loggerFactory = LoggerFactory.Create(builder => { });
        _logger = loggerFactory.CreateLogger<OrderBook>();

        _testDirectory = Path.Combine(Path.GetTempPath(), "InstrumentRepositoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        _repository = new InstrumentRepository(new NullLogger<InstrumentRepository>());
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,Spot,BTC,USDT,0.01,0.00001,1,10
BINANCE,ETHUSDT,Spot,ETH,USDT,0.01,0.0001,1,10
BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.1,0.001,1,0.001
BINANCE,ETHUSDT,PerpetualFuture,ETH,USDT,0.01,0.0001,1,0.001";

        var filePath = CreateTestCsvFile(csvContent);
        _repository.LoadFromCsv(filePath);
        _btc = _repository.FindBySymbol("BTCUSDT", ProductType.Spot, ExchangeEnum.BINANCE)!;
        _eth = _repository.FindBySymbol("ETHUSDT", ProductType.Spot, ExchangeEnum.BINANCE);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    private string CreateTestCsvFile(string content)
    {
        var filePath = Path.Combine(_testDirectory, "instruments.csv");
        File.WriteAllText(filePath, content);
        return filePath;
    }

    [Test]
    public void OrderBook_Creation_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        var orderBook = new OrderBook(_btc, _logger);

        // Assert
        orderBook.Symbol.Should().Be("BTCUSDT");
        orderBook.LastSequence.Should().Be(0);
        orderBook.UpdateCount.Should().Be(0);

        var (bidPrice, bidQty) = orderBook.GetBestBid();
        var (askPrice, askQty) = orderBook.GetBestAsk();

        bidPrice.Should().Be(0);
        bidQty.Should().Be(0);
        askPrice.Should().Be(0);
        askQty.Should().Be(0);
    }

    [Test]
    public void OrderBook_ApplyBidEvent_ShouldUpdateBestBid()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var priceTicks = PriceUtils.ToTicks(50000m); // $50,000
        var quantity = 100000000; // 1 BTC in satoshis

        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, priceTicks, quantity);

        var marketDataEvent = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            kind: EventKind.Add,
            instrumentId: symbolId,
            exchange: ExchangeEnum.BINANCE,
            updateCount: 1,
            updates: updates
        );

        // Act
        var result = orderBook.ApplyEvent(marketDataEvent);

        // Assert
        result.Should().BeTrue();
        orderBook.UpdateCount.Should().Be(1);
        orderBook.LastSequence.Should().Be(1);

        var (bidPrice, bidQty) = orderBook.GetBestBid();
        bidPrice.Should().Be(priceTicks);
        bidQty.Should().Be(quantity);
    }

    [Test]
    public void OrderBook_ApplyAskEvent_ShouldUpdateBestAsk()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var priceTicks = PriceUtils.ToTicks(50100m); // $50,100
        var quantity = 50000000; // 0.5 BTC in satoshis

        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Sell, priceTicks, quantity);

        var marketDataEvent = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            kind: EventKind.Add,
            instrumentId: symbolId,
            exchange: ExchangeEnum.BINANCE,
            updateCount: 1,
            updates: updates
        );

        // Act
        var result = orderBook.ApplyEvent(marketDataEvent);

        // Assert
        result.Should().BeTrue();

        var (askPrice, askQty) = orderBook.GetBestAsk();
        askPrice.Should().Be(priceTicks);
        askQty.Should().Be(quantity);
    }

    [Test]
    public void OrderBook_CalculateSpread_ShouldReturnCorrectValue()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;
        var bidPrice = PriceUtils.ToTicks(50000m);
        var askPrice = PriceUtils.ToTicks(50100m);
        var expectedSpread = askPrice - bidPrice;

        var bidUpdates = new PriceLevelEntryArray();
        bidUpdates[0] = new PriceLevelEntry(Side.Buy, bidPrice, 100000000);
        var askUpdates = new PriceLevelEntryArray();
        askUpdates[0] = new PriceLevelEntry(Side.Sell, askPrice, 100000000);

        // Add bid
        var bidEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: bidUpdates);
        orderBook.ApplyEvent(bidEvent);

        // Add ask
        var askEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: askUpdates);
        orderBook.ApplyEvent(askEvent);

        // Act
        var spread = orderBook.GetSpreadTicks();

        // Assert
        spread.Should().Be(expectedSpread);
    }

    [Test]
    public void OrderBook_CalculateMidPrice_ShouldReturnCorrectValue()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;
        var bidPrice = PriceUtils.ToTicks(50000m);
        var askPrice = PriceUtils.ToTicks(50100m);
        var expectedMid = (bidPrice + askPrice) / 2;

        var bidUpdates = new PriceLevelEntryArray();
        bidUpdates[0] = new PriceLevelEntry(Side.Buy, bidPrice, 100000000);
        var askUpdates = new PriceLevelEntryArray();
        askUpdates[0] = new PriceLevelEntry(Side.Sell, askPrice, 100000000);

        // Add bid and ask
        var bidEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: bidUpdates);
        var askEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: askUpdates);

        orderBook.ApplyEvent(bidEvent);
        orderBook.ApplyEvent(askEvent);

        // Act
        var midPrice = orderBook.GetMidPriceTicks();

        // Assert
        midPrice.Should().Be(expectedMid);
    }

    [Test]
    public void OrderBook_DeleteLevel_ShouldRemoveLevel()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;
        var priceTicks = PriceUtils.ToTicks(50000m);

        var addUpdates = new PriceLevelEntryArray();
        addUpdates[0] = new PriceLevelEntry(Side.Buy, priceTicks, 100000000);

        // Add bid
        var addEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: addUpdates);
        orderBook.ApplyEvent(addEvent);

        // Verify bid exists
        var (bidPrice, bidQty) = orderBook.GetBestBid();
        bidPrice.Should().Be(priceTicks);
        bidQty.Should().Be(100000000);

        var deleteUpdates = new PriceLevelEntryArray();
        deleteUpdates[0] = new PriceLevelEntry(Side.Buy, priceTicks, 0);

        // Delete the bid
        var deleteEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), EventKind.Delete, symbolId, exchange,
            updateCount: 1, updates: deleteUpdates);
        orderBook.ApplyEvent(deleteEvent);

        // Act & Assert
        var (newBidPrice, newBidQty) = orderBook.GetBestBid();
        newBidPrice.Should().Be(0);
        newBidQty.Should().Be(0);
    }

    [Test]
    public void OrderBook_ValidateIntegrity_WithValidBook_ShouldReturnTrue()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;

        var bidUpdates = new PriceLevelEntryArray();
        bidUpdates[0] = new PriceLevelEntry(Side.Buy, PriceUtils.ToTicks(50000m), 100000000);
        var askUpdates = new PriceLevelEntryArray();
        askUpdates[0] = new PriceLevelEntry(Side.Sell, PriceUtils.ToTicks(50100m), 100000000);

        var bidEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: bidUpdates);
        var askEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: askUpdates);

        orderBook.ApplyEvent(bidEvent);
        orderBook.ApplyEvent(askEvent);

        // Act
        var isValid = orderBook.ValidateIntegrity();

        // Assert
        isValid.Should().BeTrue();
    }

    [Test]
    public void OrderBook_GetSnapshot_ShouldReturnCorrectSnapshot()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;

        // Add multiple levels
        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, PriceUtils.ToTicks(50000m), 100000000);
        updates[1] = new PriceLevelEntry(Side.Buy, PriceUtils.ToTicks(49990m), 50000000);
        updates[2] = new PriceLevelEntry(Side.Sell, PriceUtils.ToTicks(50010m), 75000000);
        updates[3] = new PriceLevelEntry(Side.Sell, PriceUtils.ToTicks(50020m), 25000000);

        var batchEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 4, updates: updates);

        orderBook.ApplyEvent(batchEvent);

        // Act
        var snapshot = orderBook.GetSnapshot(5);

        // Assert
        snapshot.Symbol.Should().Be("BTCUSDT");
        snapshot.UpdateCount.Should().Be(1); // One batch event
        snapshot.Bids.Should().HaveCount(2);
        snapshot.Asks.Should().HaveCount(2);

        // Best bid should be highest price
        snapshot.Bids[0].PriceTicks.Should().Be(PriceUtils.ToTicks(50000m));
        snapshot.Bids[0].Quantity.Should().Be(100000000);

        // Best ask should be lowest price  
        snapshot.Asks[0].PriceTicks.Should().Be(PriceUtils.ToTicks(50010m));
        snapshot.Asks[0].Quantity.Should().Be(75000000);
    }

    [Test]
    public void OrderBook_ApplySnapshotEvent_ShouldClearAndRebuildBook()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;

        // 1. Add initial data
        var initialUpdates = new PriceLevelEntryArray();
        initialUpdates[0] = new PriceLevelEntry(Side.Buy, PriceUtils.ToTicks(49000m), 1_00000000);
        var initialEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange, updateCount: 1, updates: initialUpdates);
        orderBook.ApplyEvent(initialEvent);

        // 2. Prepare snapshot data
        var snapshotUpdates = new PriceLevelEntryArray();
        snapshotUpdates[0] = new PriceLevelEntry(Side.Buy, PriceUtils.ToTicks(50000m), 2_00000000);
        snapshotUpdates[1] = new PriceLevelEntry(Side.Sell, PriceUtils.ToTicks(50010m), 3_00000000);
        var snapshotEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), EventKind.Snapshot, symbolId, exchange, updateCount: 2, updates: snapshotUpdates);

        // Act
        orderBook.ApplyEvent(snapshotEvent);

        // Assert
        var (bestBidPrice, bestBidQty) = orderBook.GetBestBid();
        var (bestAskPrice, bestAskQty) = orderBook.GetBestAsk();

        // The book should be cleared and rebuilt with snapshot data
        bestBidPrice.Should().Be(PriceUtils.ToTicks(50000m));
        bestBidQty.Should().Be(2_00000000);
        bestAskPrice.Should().Be(PriceUtils.ToTicks(50010m));
        bestAskQty.Should().Be(3_00000000);

        // Check that old data is gone
        var allBids = orderBook.GetTopLevels(Side.Buy, 5).ToArray();
        allBids.Should().HaveCount(1);
        allBids[0].PriceTicks.Should().NotBe(PriceUtils.ToTicks(49000m));
    }

    [Test]
    public void OrderBook_ApplyOutOfOrderEvent_ShouldBeIgnored()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;

        var updates1 = new PriceLevelEntryArray();
        updates1[0] = new PriceLevelEntry(Side.Buy, PriceUtils.ToTicks(50000m), 1_00000000);
        var event1 = new MarketDataEvent(10, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange, updateCount: 1, updates: updates1);
        orderBook.ApplyEvent(event1);

        var updates2 = new PriceLevelEntryArray();
        updates2[0] = new PriceLevelEntry(Side.Buy, PriceUtils.ToTicks(50001m), 1_00000000);
        var outOfOrderEvent = new MarketDataEvent(9, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange, updateCount: 1, updates: updates2);

        // Act
        var result = orderBook.ApplyEvent(outOfOrderEvent);

        // Assert
        result.Should().BeFalse();
        orderBook.LastSequence.Should().Be(10);
        var (bestBid, _) = orderBook.GetBestBid();
        bestBid.Should().Be(PriceUtils.ToTicks(50000m)); // Should not be updated by the out-of-order event
    }
}
