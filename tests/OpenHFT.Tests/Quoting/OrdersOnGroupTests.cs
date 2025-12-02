using System;
using System.Collections.Generic;
using System.Linq;
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
using OpenHFT.Quoting;
using OpenHFT.Quoting.Models;
using OpenHFT.Processing;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Models;

namespace OpenHFT.Tests.Quoting;

// Test Helper Order Implementation
public class TestOrder : IOrder, IOrderSettable
{
    public long ClientOrderId { get; set; } = new Random().Next();
    public string ExchangeOrderId { get; set; } = "";
    public int InstrumentId { get; set; }
    public Side Side { get; set; }
    public Price Price { get; set; }
    public Quantity Quantity { get; set; }
    public Quantity LeavesQuantity { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.New;
    public string BookName { get; set; } = "";
    public bool IsPostOnly { get; set; }
    public OrderType OrderType { get; set; }

    public long LastUpdateTime => 0;

    public OrderStatusReport? LatestReport => throw new NotImplementedException();

    public event EventHandler<OrderStatusReport>? StatusChanged;
    public event EventHandler<Fill>? OrderFilled;

    public Task SubmitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        Status = OrderStatus.Cancelled;
        var osr = new OrderStatusReport(ClientOrderId, "123", null, InstrumentId, Side, OrderStatus.Cancelled, Price, Quantity, Quantity, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        StatusChanged?.Invoke(this, osr);
        return Task.CompletedTask;
    }

    public Task ReplaceAsync(Price newPrice, OrderType newOrderType, CancellationToken cancellationToken = default)
    {
        Price = newPrice;
        return Task.CompletedTask;
    }

    public void SimulateFill(Quantity fillQty)
    {
        LeavesQuantity -= fillQty;
        var status = LeavesQuantity.ToDecimal() <= 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
        Status = status;

        OrderFilled?.Invoke(this, new Fill(InstrumentId, BookName, ClientOrderId, ExchangeOrderId, "Exec1", Side, Price, fillQty, 0));
        var osr = new OrderStatusReport(ClientOrderId, "123", null, InstrumentId, Side, status, Price, Quantity, LeavesQuantity, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        StatusChanged?.Invoke(this, osr);
    }

    public void Dispose() { }

    public void AddStatusChangedHandler(EventHandler<OrderStatusReport> handler)
    {
        StatusChanged += handler;
    }
}

[TestFixture]
public class OrdersOnGroupTests
{
    private Mock<ILogger> _mockLogger;
    private Mock<IOrderFactory> _mockOrderFactory;
    private Mock<IMarketDataManager> _mockMarketDataManager;
    private CryptoPerpetual _instrument;

    private List<TestOrder> _createdOrders;
    private OrderBook _orderBook;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger>();
        _mockOrderFactory = new Mock<IOrderFactory>();
        _mockMarketDataManager = new Mock<IMarketDataManager>();
        _createdOrders = new List<TestOrder>();

        // Instrument: TickSize 0.5
        _instrument = new CryptoPerpetual(1, "XBTUSD", ExchangeEnum.BITMEX, Currency.BTC, Currency.USD,
            Price.FromDecimal(0.5m), Quantity.FromDecimal(1), 1m, Quantity.FromDecimal(1));

        // OrderFactory Mock
        _mockOrderFactory.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<Side>(), It.IsAny<string>()))
            .Returns((int id, Side side, string book) =>
            {
                var order = new TestOrder
                {
                    InstrumentId = id,
                    Side = side,
                    BookName = book,
                    Quantity = Quantity.FromDecimal(100),
                    LeavesQuantity = Quantity.FromDecimal(100)
                };
                order.AddStatusChangedHandler((sender, report) =>
                {
                    if (report.Status == OrderStatus.Cancelled || report.Status == OrderStatus.Filled)
                    {
                        _createdOrders.Remove(order);
                    }
                });
                _createdOrders.Add(order);
                return order;
            });

        // OrderBook 초기화 및 Mock 연결
        _orderBook = new OrderBook(_instrument, null);
        _mockMarketDataManager.Setup(m => m.GetOrderBook(_instrument.InstrumentId)).Returns(_orderBook);

        // Default Market Data (Bid 10000, Ask 10005)
        UpdateMarketData(10000m, 10005m);
    }

    private void UpdateMarketData(decimal bestBid, decimal bestAsk)
    {
        // Snapshot 생성 및 적용
        // Note: PriceLevelEntryArray is inline array, requires specific handling or helper if available.
        // Assuming we can populate it or use `MarketDataEvent` constructor that takes updates.

        // Using unsafe/span approach or helper if `PriceLevelEntryArray` is tricky to instantiate directly.
        // Since this is test code, we assume we can just create it.

        // *Important*: If PriceLevelEntryArray is `ref struct` or inline array, we need to be careful.
        // Here assuming we can use indexer (C# 12 feature) or Unsafe.

        var updates = new PriceLevelEntryArray();
        // C# 12 inline array indexer
        // updates[0] = new PriceLevelEntry(Side.Buy, bestBid, 10000);
        // updates[1] = new PriceLevelEntry(Side.Sell, bestAsk, 10000);

        // If indexer not available, use Span casting
        Span<PriceLevelEntry> span = updates;
        span[0] = new PriceLevelEntry(Side.Buy, bestBid, 10000);
        span[1] = new PriceLevelEntry(Side.Sell, bestAsk, 10000);

        var evt = new MarketDataEvent(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), EventKind.Snapshot,
            _instrument.InstrumentId, _instrument.SourceExchange, 0, 0, 2, updates);

        _orderBook.ApplyEvent(evt);
    }

    private OrdersOnGroup CreateGroup(Side side, int depth, decimal groupingBp)
    {
        return new OrdersOnGroup(
            _mockLogger.Object, _instrument, side,
            _mockOrderFactory.Object, "TestBook",
            _mockMarketDataManager.Object, depth, groupingBp);
    }

    // ==========================================================================
    // 1. Basic Layering Test (Buy Side)
    // ==========================================================================
    [Test]
    public async Task UpdateAsync_BuySide_ShouldCreateOrdersFromOuterToInner()
    {
        // Arrange: GroupingBp = 100bp (1%). Price = 10000. GroupRange = 100.
        // Depth = 2. Step = 50. Buy Prices: 10000 (Inner), 9950 (Outer)

        var group = CreateGroup(Side.Buy, 2, 100.0m);
        var quote = new Quote(Price.FromDecimal(10000m), Quantity.FromDecimal(100));

        // Act 1
        await group.UpdateAsync(quote, HittingLogic.AllowAll, false, CancellationToken.None);
        // Assert 1: Outer (9950) created first
        _createdOrders.Count.Should().Be(1);
        _createdOrders[0].Price.ToDecimal().Should().Be(9950m);

        // Act 2
        await group.UpdateAsync(quote, HittingLogic.AllowAll, false, CancellationToken.None);
        // Assert 2: Inner (10000) created second
        _createdOrders.Count.Should().Be(2);
        _createdOrders[1].Price.ToDecimal().Should().Be(10000m);
    }

    // ==========================================================================
    // 2. Hitting Logic Test (Pennying)
    // ==========================================================================
    [Test]
    public async Task UpdateAsync_WithPennying_ShouldAdjustPriceToBestBidPlusTick()
    {
        // Arrange
        // Market: BestBid=10000, BestAsk=10005.
        // Quote: 10002 (Crosses BestBid 10000).
        // Pennying Logic: If Quote > BestBid -> BestBid + 1 Tick = 10000.5

        UpdateMarketData(10000m, 10005m);
        var group = CreateGroup(Side.Buy, 1, 100.0m);
        var quote = new Quote(Price.FromDecimal(10002m), Quantity.FromDecimal(100));

        // Act
        await group.UpdateAsync(quote, HittingLogic.Pennying, false, CancellationToken.None);

        // Assert
        _createdOrders.Count.Should().Be(1);
        _createdOrders[0].Price.ToDecimal().Should().Be(10000.5m); // 10000 + 0.5
    }

    // ==========================================================================
    // 3. Hitting Logic Test (OurBest)
    // ==========================================================================
    [Test]
    public async Task UpdateAsync_WithOurBest_ShouldCapPriceAtBestBid()
    {
        // Arrange
        // Market: BestBid=10000
        // Quote: 10002 (Crosses BestBid)
        // OurBest Logic: Cap at BestBid (10000)

        UpdateMarketData(10000m, 10005m);
        var group = CreateGroup(Side.Buy, 1, 100.0m);
        var quote = new Quote(Price.FromDecimal(10002m), Quantity.FromDecimal(100));

        // Act
        await group.UpdateAsync(quote, HittingLogic.OurBest, false, CancellationToken.None);

        // Assert
        _createdOrders.Count.Should().Be(1);
        _createdOrders[0].Price.ToDecimal().Should().Be(10000m);
    }

    // ==========================================================================
    // 4. Target Price Change (Move) Test
    // ==========================================================================
    [Test]
    public async Task UpdateAsync_WhenTargetPriceChanges_ShouldCancelAllBeforeNewOrders()
    {
        var group = CreateGroup(Side.Buy, 2, 100.0m);
        await group.UpdateAsync(new Quote(Price.FromDecimal(10000m), Quantity.FromDecimal(100)), HittingLogic.AllowAll, false, CancellationToken.None);
        var oldOrder = _createdOrders.Single();

        // Act 1: Change Target -> Trigger Cancel Mode
        await group.UpdateAsync(new Quote(Price.FromDecimal(11000m), Quantity.FromDecimal(100)), HittingLogic.AllowAll, false, CancellationToken.None);

        // Assert 1: Old order cancelled
        oldOrder.Status.Should().Be(OrderStatus.Cancelled);
        _createdOrders.Count.Should().Be(1);

        // Act 2: Next Tick -> Create New Order
        await group.UpdateAsync(new Quote(Price.FromDecimal(11000m), Quantity.FromDecimal(100)), HittingLogic.AllowAll, false, CancellationToken.None);

        // Assert 2
        _createdOrders.Count.Should().Be(2);
        _createdOrders.Last().Price.ToDecimal().Should().Be(11000m);
    }

    // ==========================================================================
    // 5. Replace Logic Test
    // ==========================================================================
    [Test]
    public async Task UpdateAsync_WhenHittingLogicShiftsPrice_ShouldReplaceOrder()
    {
        // Arrange: Order at 10000.5 (Pennying from previous state)
        UpdateMarketData(10000m, 10005m);
        var group = CreateGroup(Side.Buy, 1, 100.0m);
        var quote = new Quote(Price.FromDecimal(10002m), Quantity.FromDecimal(100)); // Intended 10002

        // First tick: Pennying sets price to 10000.5
        await group.UpdateAsync(quote, HittingLogic.Pennying, false, CancellationToken.None);
        var order = _createdOrders.Single();
        order.Price.ToDecimal().Should().Be(10000.5m);

        // Act: Market moves up! BestBid -> 10001.
        // Pennying should now target 10001 + 0.5 = 10001.5
        // TargetQuotePrice (10002) is UNCHANGED, so no CancelAll triggered.
        // Reconcile should detect price diff and Replace.

        UpdateMarketData(10001m, 10006m);
        await group.UpdateAsync(quote, HittingLogic.Pennying, false, CancellationToken.None);

        // Assert
        // Order object should be same instance, but Price updated
        order.Price.ToDecimal().Should().Be(10001.5m);
        // Verify Replace called? (We can check if a new order was created -> No)
        _createdOrders.Count.Should().Be(1);
    }
}