using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.DataBase;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Positions;
using OpenHFT.Processing;
using OpenHFT.Service;

namespace OpenHFT.Tests.Core.Positions;

public class PositionManagerTests
{
    private ServiceProvider _serviceProvider;
    private string _testDirectory;
    private IInstrumentRepository _instrumentRepo;
    private IOrderRouter _orderRouter;
    private IOrderFactory _orderFactory;
    private IFillRepository _fillRepository;

    [SetUp]
    public void SetUp()
    {
        // --- 1. 테스트 환경 설정 (임시 DB 파일 경로) ---
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

        // --- 2. 실제 구현체들 등록 ---
        // InstrumentRepository 설정
        _testDirectory = Path.Combine(Path.GetTempPath(), "InstrumentRepositoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        var filePath = Path.Combine(_testDirectory, "instruments.csv");
        var csvContent = @"instrument_id,market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
    1,BITMEX,XBTUSDT,perpetualfuture,XBTUSDT,USDT,1,0.1,100,XBTUSDT,0.0001";
        File.WriteAllText(filePath, csvContent);

        var inMemorySettings = new Dictionary<string, string>
        {
            { "dataFolder", _testDirectory },
            { "FILL_DB_PATH", _testDirectory}
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        services.AddLogging(builder => builder.AddConsole()); // AddConsole() 사용
        services.AddSingleton(configuration);
        services.AddSingleton<IInstrumentRepository, InstrumentRepository>();
        services.AddSingleton(new SubscriptionConfig());
        services.AddSingleton<MarketDataDistributor>();
        services.AddSingleton<IMarketDataManager, MarketDataManager>();

        // Order 파이프라인 등록
        services.AddSingleton<IOrderRouter, OrderRouter>();

        // IOrderGateway는 Mocking하여 실제 주문 전송 방지
        var mockGateway = new Mock<IOrderGateway>();
        var mockGatewayRegistry = new Mock<IOrderGatewayRegistry>();
        mockGatewayRegistry.Setup(r => r.GetGatewayForInstrument(It.IsAny<int>())).Returns(mockGateway.Object);
        services.AddSingleton(mockGatewayRegistry.Object);

        services.AddSingleton<IOrderFactory, OrderFactory>();
        services.AddSingleton<IClientIdGenerator, ClientIdGenerator>();

        // Persistence 계층 등록
        services.AddSingleton<IFillRepository, SqliteFillRepository>();

        // PositionManager 등록 (IHostedService가 아닌 일반 싱글톤으로)
        services.AddSingleton<PositionManager>();
        services.AddSingleton<IPositionManager>(p => p.GetRequiredService<PositionManager>());
        services.AddHostedService<FillPersistenceService>();

        _serviceProvider = services.BuildServiceProvider();

        _instrumentRepo = _serviceProvider.GetRequiredService<IInstrumentRepository>();
        _orderRouter = _serviceProvider.GetRequiredService<IOrderRouter>();
        _orderFactory = _serviceProvider.GetRequiredService<IOrderFactory>();
        _fillRepository = _serviceProvider.GetRequiredService<IFillRepository>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Test]
    public async Task PositionManager_ShouldPersistFills_AndRestorePositionOnRestart()
    {
        var instrument = _instrumentRepo.GetById(1)!;

        // --- 1. Arrange & Act: 체결 데이터 생성 및 저장 ---
        TestContext.WriteLine("Step 1: Simulating fills and letting PositionManager persist them...");
        var fillPersistenceService = _serviceProvider.GetServices<IHostedService>().OfType<FillPersistenceService>().First();
        await fillPersistenceService.StartAsync(CancellationToken.None);

        // PositionManager가 OrderRouter 이벤트를 구독하도록 시작
        var initialManager = _serviceProvider.GetRequiredService<PositionManager>();

        // Order 객체 생성 (자동으로 OrderRouter에 등록됨)
        var order = _orderFactory.Create(instrument.InstrumentId, Side.Buy, "test", OrderSource.NonManual);

        // 가짜 체결 리포트 2개 생성
        var fillReport1 = new OrderStatusReport(
            order.ClientOrderId, "exo1", "exec1", instrument.InstrumentId, Side.Buy,
            OrderStatus.PartiallyFilled, Price.FromDecimal(50000m), Quantity.FromDecimal(10m), Quantity.FromDecimal(5m),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lastQuantity: Quantity.FromDecimal(5m), lastPrice: Price.FromDecimal(50010m));

        var fillReport2 = new OrderStatusReport(
            order.ClientOrderId, "exo1", "exec2", instrument.InstrumentId, Side.Buy,
            OrderStatus.Filled, Price.FromDecimal(50010m), Quantity.FromDecimal(10m), Quantity.FromDecimal(0m),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 100, lastQuantity: Quantity.FromDecimal(5m), lastPrice: Price.FromDecimal(50000m));

        // OrderRouter를 통해 리포트 주입 -> Order.OnStatusReportReceived -> router.RaiseOrderFilled -> positionManager.OnOrderFilled
        _orderRouter.RouteReport(fillReport1);
        _orderRouter.RouteReport(fillReport2);

        // PositionManager와 SqliteRepository가 비동기적으로 처리할 시간을 줌
        await Task.Delay(500);

        // --- 2. Assert: 데이터가 DB에 저장되었는지 확인 ---
        TestContext.WriteLine("Step 2: Verifying fills are persisted in the database...");
        var savedFills = await _fillRepository.GetFillsByDateAsync(DateTime.UtcNow.Date);
        savedFills.Should().HaveCount(2);
        savedFills.Any(f => f.ExecutionId == "exec1").Should().BeTrue();
        savedFills.Any(f => f.ExecutionId == "exec2").Should().BeTrue();

        var finalPosition = initialManager.GetPosition(instrument.InstrumentId);
        TestContext.WriteLine($" -> Final position before restart: {finalPosition}");

        // --- 3. Act: 상태 복원 시뮬레이션 (새로운 PositionManager 생성) ---
        TestContext.WriteLine("\nStep 3: Simulating restart by creating a new PositionManager...");

        // 새로운 Manager는 동일한 DB 파일을 사용하지만, 메모리 상태는 비어 있음
        var restoredManager = new PositionManager(
            _serviceProvider.GetRequiredService<ILogger<PositionManager>>(),
            _orderRouter,
            _fillRepository
        );

        // 복원 로직 실행
        await restoredManager.RestorePositionsAsync();

        // --- 4. Assert: 포지션이 올바르게 복원되었는지 확인 ---
        TestContext.WriteLine("Step 4: Verifying position is restored correctly...");
        var restoredPosition = restoredManager.GetPosition(instrument.InstrumentId);

        restoredPosition.Should().NotBeNull();

        // 최종 포지션과 복원된 포지션이 동일한지 비교
        restoredPosition.Quantity.Should().Be(finalPosition.Quantity);
        restoredPosition.AverageEntryPrice.Should().Be(finalPosition.AverageEntryPrice);

        TestContext.WriteLine($" -> Restored position: {restoredPosition}");
        finalPosition.Quantity.ToDecimal().Should().Be(10m);
        finalPosition.AverageEntryPrice.ToDecimal().Should().Be(50005m); // (5*50000 + 5*50010) / 10
    }
}