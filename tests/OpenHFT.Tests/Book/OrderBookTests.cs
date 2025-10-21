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

        var (bid, bidQty) = orderBook.GetBestBid();
        var (ask, askQty) = orderBook.GetBestAsk();

        bid.ToTicks().Should().Be(0);
        bidQty.ToTicks().Should().Be(0);
        ask.ToTicks().Should().Be(0);
        askQty.ToTicks().Should().Be(0);
    }

    [Test]
    public void OrderBook_ApplyBidEvent_ShouldUpdateBestBid()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var price = 50000m; // $50,000
        var quantity = 1m; // 1 BTC

        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, price, quantity);

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

        var (bid, bidQty) = orderBook.GetBestBid();
        bid.Should().Be(Price.FromDecimal(price));
        bidQty.Should().Be(Quantity.FromDecimal(quantity));
    }

    [Test]
    public void OrderBook_ApplyAskEvent_ShouldUpdateBestAsk()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var price = 50100m; // $50,100
        var quantity = 0.5m; // 0.5 BTC

        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Sell, price, quantity);

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

        var (ask, askQty) = orderBook.GetBestAsk();
        ask.Should().Be(Price.FromDecimal(price));
        askQty.Should().Be(Quantity.FromDecimal(quantity));
    }

    [Test]
    public void OrderBook_CalculateSpread_ShouldReturnCorrectValue()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;
        var bidPrice = 50000m;
        var askPrice = 50100m;
        var expectedSpread = askPrice - bidPrice;

        var bidUpdates = new PriceLevelEntryArray();
        bidUpdates[0] = new PriceLevelEntry(Side.Buy, bidPrice, 1m);
        var askUpdates = new PriceLevelEntryArray();
        askUpdates[0] = new PriceLevelEntry(Side.Sell, askPrice, 1m);

        // Add bid
        var bidEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: bidUpdates);
        orderBook.ApplyEvent(bidEvent);

        // Add ask
        var askEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: askUpdates);
        orderBook.ApplyEvent(askEvent);

        // Act
        var spread = orderBook.GetSpread();

        // Assert
        spread.Should().Be(Price.FromDecimal(expectedSpread));
    }

    [Test]
    public void OrderBook_CalculateMidPrice_ShouldReturnCorrectValue()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;
        var bidPrice = 50000m;
        var askPrice = 50100m;
        var expectedMid = Price.FromDecimal((bidPrice + askPrice) / 2m);

        var bidUpdates = new PriceLevelEntryArray();
        bidUpdates[0] = new PriceLevelEntry(Side.Buy, bidPrice, 1m);
        var askUpdates = new PriceLevelEntryArray();
        askUpdates[0] = new PriceLevelEntry(Side.Sell, askPrice, 1m);

        // Add bid and ask
        var bidEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: bidUpdates);
        var askEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: askUpdates);

        orderBook.ApplyEvent(bidEvent);
        orderBook.ApplyEvent(askEvent);

        // Act
        var midPrice = orderBook.GetMidPrice();

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
        var price = 50000m;
        var quantity = 1m;

        var addUpdates = new PriceLevelEntryArray();
        addUpdates[0] = new PriceLevelEntry(Side.Buy, price, quantity);

        // Add bid
        var addEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 1, updates: addUpdates);
        orderBook.ApplyEvent(addEvent);

        // Verify bid exists
        var (bid, bidQty) = orderBook.GetBestBid();
        bid.Should().Be(Price.FromDecimal(price));
        bidQty.Should().Be(Quantity.FromDecimal(quantity));

        var deleteUpdates = new PriceLevelEntryArray();
        deleteUpdates[0] = new PriceLevelEntry(Side.Buy, price, 0);

        // Delete the bid
        var deleteEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), EventKind.Delete, symbolId, exchange,
            updateCount: 1, updates: deleteUpdates);
        orderBook.ApplyEvent(deleteEvent);

        // Act & Assert
        var (newBid, newBidQty) = orderBook.GetBestBid();
        newBid.ToTicks().Should().Be(0);
        newBidQty.ToTicks().Should().Be(0);
    }

    [Test]
    public void OrderBook_ValidateIntegrity_WithValidBook_ShouldReturnTrue()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;

        var bidUpdates = new PriceLevelEntryArray();
        bidUpdates[0] = new PriceLevelEntry(Side.Buy, 50000m, 1m);
        var askUpdates = new PriceLevelEntryArray();
        askUpdates[0] = new PriceLevelEntry(Side.Sell, 50100m, 1m);

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
        updates[0] = new PriceLevelEntry(Side.Buy, 50000m, 1m);
        updates[1] = new PriceLevelEntry(Side.Buy, 49990m, 0m);
        updates[2] = new PriceLevelEntry(Side.Sell, 50010m, 0m);
        updates[3] = new PriceLevelEntry(Side.Sell, 50020m, 0m);

        var batchEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange,
            updateCount: 4, updates: updates);

        orderBook.ApplyEvent(batchEvent);

        // Act
        var snapshot = orderBook.GetSnapshot(5);

        // Assert
        snapshot.Symbol.Should().Be("BTCUSDT");
        snapshot.UpdateCount.Should().Be(1); // One batch event
        snapshot.Bids.Should().HaveCount(1);
        snapshot.Asks.Should().HaveCount(0);

        // Best bid should be highest price
        snapshot.Bids[0].PriceTicks.Should().Be(Price.FromDecimal(50000m).ToTicks());
        snapshot.Bids[0].Quantity.Should().Be(Quantity.FromDecimal(1m).ToTicks());
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
        initialUpdates[0] = new PriceLevelEntry(Side.Buy, 49000m, 1m);
        var initialEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange, updateCount: 1, updates: initialUpdates);
        orderBook.ApplyEvent(initialEvent);

        // 2. Prepare snapshot data
        var snapshotUpdates = new PriceLevelEntryArray();
        snapshotUpdates[0] = new PriceLevelEntry(Side.Buy, 50000m, 2m);
        snapshotUpdates[1] = new PriceLevelEntry(Side.Sell, 50010m, 3m);
        var snapshotEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), EventKind.Snapshot, symbolId, exchange, updateCount: 2, updates: snapshotUpdates);

        // Act
        orderBook.ApplyEvent(snapshotEvent);

        // Assert
        var (bestBid, bestBidQty) = orderBook.GetBestBid();
        var (bestAsk, bestAskQty) = orderBook.GetBestAsk();

        // The book should be cleared and rebuilt with snapshot data
        bestBid.Should().Be(Price.FromDecimal(50000m));
        bestBidQty.Should().Be(Quantity.FromDecimal(2m));
        bestAsk.Should().Be(Price.FromDecimal(50010m));
        bestAskQty.Should().Be(Quantity.FromDecimal(3m));

        // Check that old data is gone
        var allBids = orderBook.GetTopLevels(Side.Buy, 5).ToArray();
        allBids.Should().HaveCount(1);
        allBids[0].Price.Should().NotBe(Price.FromDecimal(49000m));
    }

    [Test]
    public void OrderBook_ApplyOutOfOrderEvent_ShouldBeIgnored()
    {
        // Arrange
        var orderBook = new OrderBook(_btc, _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;

        var updates1 = new PriceLevelEntryArray();
        updates1[0] = new PriceLevelEntry(Side.Buy, 50000m, 1m);
        var event1 = new MarketDataEvent(10, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange, updateCount: 1, updates: updates1);
        orderBook.ApplyEvent(event1);

        var updates2 = new PriceLevelEntryArray();
        updates2[0] = new PriceLevelEntry(Side.Buy, 50001m, 1m);
        var outOfOrderEvent = new MarketDataEvent(9, TimestampUtils.GetTimestampMicros(), EventKind.Add, symbolId, exchange, updateCount: 1, updates: updates2);

        // Act
        var result = orderBook.ApplyEvent(outOfOrderEvent);

        // Assert
        result.Should().BeFalse();
        orderBook.LastSequence.Should().Be(10);
        var (bestBid, _) = orderBook.GetBestBid();
        bestBid.Should().Be(Price.FromDecimal(50000m)); // Should not be updated by the out-of-order event
    }
}
