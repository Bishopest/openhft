using NUnit.Framework;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Book.Core;

namespace OpenHFT.Tests.Book;

[TestFixture]
public class OrderBookTests
{
    private ILogger<OrderBook> _logger;

    [SetUp]
    public void Setup()
    {
        var loggerFactory = LoggerFactory.Create(builder => { });
        _logger = loggerFactory.CreateLogger<OrderBook>();
    }

    [Test]
    public void OrderBook_Creation_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        var orderBook = new OrderBook("BTCUSDT", _logger);

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
        var orderBook = new OrderBook("BTCUSDT", _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var priceTicks = PriceUtils.ToTicks(50000m); // $50,000
        var quantity = 100000000; // 1 BTC in satoshis

        var marketDataEvent = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            side: Side.Buy,
            priceTicks: priceTicks,
            quantity: quantity,
            kind: EventKind.Add,
            instrumentId: symbolId,
            exchange: ExchangeEnum.BINANCE
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
        var orderBook = new OrderBook("BTCUSDT", _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var priceTicks = PriceUtils.ToTicks(50100m); // $50,100
        var quantity = 50000000; // 0.5 BTC in satoshis

        var marketDataEvent = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            side: Side.Sell,
            priceTicks: priceTicks,
            quantity: quantity,
            kind: EventKind.Add,
            instrumentId: symbolId,
            exchange: ExchangeEnum.BINANCE
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
        var orderBook = new OrderBook("BTCUSDT", _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;
        var bidPrice = PriceUtils.ToTicks(50000m);
        var askPrice = PriceUtils.ToTicks(50100m);
        var expectedSpread = askPrice - bidPrice;

        // Add bid
        var bidEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), Side.Buy, bidPrice, 100000000, EventKind.Add, symbolId, exchange);
        orderBook.ApplyEvent(bidEvent);

        // Add ask
        var askEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), Side.Sell, askPrice, 100000000, EventKind.Add, symbolId, exchange);
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
        var orderBook = new OrderBook("BTCUSDT", _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;
        var bidPrice = PriceUtils.ToTicks(50000m);
        var askPrice = PriceUtils.ToTicks(50100m);
        var expectedMid = (bidPrice + askPrice) / 2;

        // Add bid and ask
        var bidEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), Side.Buy, bidPrice, 100000000, EventKind.Add, symbolId, exchange);
        var askEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), Side.Sell, askPrice, 100000000, EventKind.Add, symbolId, exchange);

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
        var orderBook = new OrderBook("BTCUSDT", _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;
        var priceTicks = PriceUtils.ToTicks(50000m);

        // Add bid
        var addEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), Side.Buy, priceTicks, 100000000, EventKind.Add, symbolId, exchange);
        orderBook.ApplyEvent(addEvent);

        // Verify bid exists
        var (bidPrice, bidQty) = orderBook.GetBestBid();
        bidPrice.Should().Be(priceTicks);
        bidQty.Should().Be(100000000);

        // Delete the bid
        var deleteEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), Side.Buy, priceTicks, 0, EventKind.Delete, symbolId, exchange);
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
        var orderBook = new OrderBook("BTCUSDT", _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;

        var bidEvent = new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), Side.Buy, PriceUtils.ToTicks(50000m), 100000000, EventKind.Add, symbolId, exchange);
        var askEvent = new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), Side.Sell, PriceUtils.ToTicks(50100m), 100000000, EventKind.Add, symbolId, exchange);

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
        var orderBook = new OrderBook("BTCUSDT", _logger);
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var exchange = ExchangeEnum.BINANCE;
        // Add multiple levels
        var events = new[]
        {
            new MarketDataEvent(1, TimestampUtils.GetTimestampMicros(), Side.Buy, PriceUtils.ToTicks(50000m), 100000000, EventKind.Add, symbolId, exchange),
            new MarketDataEvent(2, TimestampUtils.GetTimestampMicros(), Side.Buy, PriceUtils.ToTicks(49990m), 50000000, EventKind.Add, symbolId, exchange),
            new MarketDataEvent(3, TimestampUtils.GetTimestampMicros(), Side.Sell, PriceUtils.ToTicks(50010m), 75000000, EventKind.Add, symbolId, exchange),
            new MarketDataEvent(4, TimestampUtils.GetTimestampMicros(), Side.Sell, PriceUtils.ToTicks(50020m), 25000000, EventKind.Add, symbolId, exchange)
        };

        foreach (var evt in events)
        {
            orderBook.ApplyEvent(evt);
        }

        // Act
        var snapshot = orderBook.GetSnapshot(5);

        // Assert
        snapshot.Symbol.Should().Be("BTCUSDT");
        snapshot.UpdateCount.Should().Be(4);
        snapshot.Bids.Should().HaveCount(2);
        snapshot.Asks.Should().HaveCount(2);

        // Best bid should be highest price
        snapshot.Bids[0].PriceTicks.Should().Be(PriceUtils.ToTicks(50000m));
        snapshot.Bids[0].Quantity.Should().Be(100000000);

        // Best ask should be lowest price  
        snapshot.Asks[0].PriceTicks.Should().Be(PriceUtils.ToTicks(50010m));
        snapshot.Asks[0].Quantity.Should().Be(75000000);
    }
}
