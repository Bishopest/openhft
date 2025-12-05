using System.Threading.Tasks;
using Disruptor.Dsl;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Feed;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Processing;

namespace OpenHFT.Tests.Processing;

public class OrderUpdateDistributorTests
{
    private ServiceProvider _serviceProvider = null!;
    private MockAdapter _mockAdapter = null!;
    private TestOrder _testOrder = null!;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<IOrderRouter, OrderRouter>();

        _mockAdapter = new MockAdapter(new NullLogger<MockAdapter>(), ProductType.PerpetualFuture, null, ExecutionMode.Testnet);
        services.AddSingleton<IFeedAdapter>(_mockAdapter);
        services.AddSingleton<IFeedAdapterRegistry, FeedAdapterRegistry>();
        services.AddSingleton<IOrderUpdateHandler, OrderUpdateDistributor>();

        services.AddSingleton(provider =>
        {
            var disruptor = new Disruptor<OrderStatusReportWrapper>(() => new OrderStatusReportWrapper(), 1024);
            disruptor.HandleEventsWith(provider.GetRequiredService<IOrderUpdateHandler>());
            return disruptor;
        });

        services.AddSingleton(new Disruptor<MarketDataEventWrapper>(() => new MarketDataEventWrapper(), 1024));
        services.AddSingleton<IFeedHandler, FeedHandler>();

        _serviceProvider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
    }

    [Test]
    public async Task Distributor_ShouldRouteReport_ToOrderRouter()
    {
        var disruptor = _serviceProvider.GetRequiredService<Disruptor<OrderStatusReportWrapper>>();
        var orderRouter = _serviceProvider.GetRequiredService<IOrderRouter>();
        _serviceProvider.GetRequiredService<IFeedHandler>();

        const long testClientOrderId = 12345L;
        _testOrder = new TestOrder(testClientOrderId, 101, "test", Side.Buy, orderRouter);

        var ringBufer = disruptor.Start();

        var testReport = new OrderStatusReport(clientOrderId: 12345L, exchangeOrderId: "xyz-789", executionId: null, instrumentId: 101, side: Side.Buy, status: OrderStatus.New, price: Price.FromDecimal(5000m),
        quantity: Quantity.FromDecimal(1m), leavesQuantity: Quantity.FromDecimal(1m), timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _mockAdapter.FireOrderUpdateEvent(testReport);

        await Task.Delay(200);

        _testOrder.ReportsReceivedCount.Should().Be(1);

        _testOrder.LastReceivedReport.Should().NotBeNull();

        var receivedReport = _testOrder.LastReceivedReport!.Value;
        receivedReport.ClientOrderId.Should().Be(testClientOrderId);
        receivedReport.ExchangeOrderId.Should().Be("xyz-789");
        receivedReport.InstrumentId.Should().Be(101);
        receivedReport.Status.Should().Be(OrderStatus.New);
    }
}

/// <summary>
/// A test implementation of IOrder that captures the last received status report.
/// </summary>
public class TestOrder : IOrder, IOrderUpdatable
{
    private readonly IOrderRouter _router;
    public long ClientOrderId { get; }
    public int InstrumentId { get; }
    public string BookName { get; }
    public Side Side { get; }

    // --- Public properties to inspect results ---
    public OrderStatusReport? LastReceivedReport { get; private set; }
    public int ReportsReceivedCount { get; private set; }

    public TestOrder(long clientOrderId, int instrumentId, string bookName, Side side, IOrderRouter router)
    {
        ClientOrderId = clientOrderId;
        InstrumentId = instrumentId;
        BookName = bookName;
        Side = side;
        _router = router;

        // Automatically register itself
        _router.RegisterOrder(this);
    }

    // --- IOrderUpdatable Implementation ---
    public void OnStatusReportReceived(in OrderStatusReport report)
    {
        LastReceivedReport = report;
        ReportsReceivedCount++;

        // In a real order, you would check for terminal state and deregister.
        // For testing, we keep it simple.
    }

    #region Unused IOrder Members
    public string? ExchangeOrderId => null;
    public OrderStatus Status => LastReceivedReport?.Status ?? OrderStatus.Pending;
    public Price Price => default;
    public Quantity Quantity => default;
    public Quantity LeavesQuantity => default;
    public long LastUpdateTime => 0;
    public OrderStatusReport? LatestReport => LastReceivedReport;

    public bool IsPostOnly => true;

    Price IOrder.Price { get => Price; set => throw new NotImplementedException(); }
    Quantity IOrder.Quantity { get => Quantity; set => throw new NotImplementedException(); }
    Quantity IOrder.LeavesQuantity { get => LeavesQuantity; set => throw new NotImplementedException(); }
    bool IOrder.IsPostOnly { get => IsPostOnly; set => throw new NotImplementedException(); }
    public OrderType OrderType { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public event EventHandler<OrderStatusReport>? StatusChanged;
    public event EventHandler<Fill>? OrderFilled;

    public Task SubmitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReplaceAsync(Price price, OrderType orderType, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReconcileStateAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public void AddStatusChangedHandler(EventHandler<OrderStatusReport> handler)
    {
        throw new NotImplementedException();
    }

    public void RemoveStatusChangedHandler(EventHandler<OrderStatusReport> handler)
    {
        throw new NotImplementedException();
    }

    public void AddFillHandler(EventHandler<Fill> handler)
    {
        throw new NotImplementedException();
    }

    public void RemoveFillHandler(EventHandler<Fill> handler)
    {
        throw new NotImplementedException();
    }

    public void ResetState()
    {
        throw new NotImplementedException();
    }
    #endregion
}