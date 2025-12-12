using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using FluentAssertions;
using Moq;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Processing;
using OpenHFT.Quoting;
using OpenHFT.Quoting.FairValue;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;
using OpenHFT.Quoting.Validators;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Orders;
using Disruptor.Dsl;
using Microsoft.Extensions.Logging.Abstractions;
using OpenHFT.Gateway;

namespace OpenHFT.Tests.Quoting;

[TestFixture, Category("Integration")]
public class QuotingInstanceManagerTests_Integration
{
    private ServiceProvider _serviceProvider;
    private IQuotingInstanceManager _manager;
    private IInstrumentRepository _instrumentRepo;
    private string _testDirectory;
    private Mock<IFeedHandler> _mockFeedHandler;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();

        // --- 1. 기본 서비스 등록 ---
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

        // --- 2. InstrumentRepository 설정 (요청사항 반영) ---
        _testDirectory = Path.Combine(Path.GetTempPath(), "QuotingInstanceManagerTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        var csvContent = @"instrument_id,market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
1,BITMEX,XBTUSD,PerpetualFuture,XBT,USD,0.5,1,1,1
2,BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.01,0.001,1,0.001";
        File.WriteAllText(Path.Combine(_testDirectory, "instruments.csv"), csvContent);

        var inMemoryConfig = new Dictionary<string, string> { { "dataFolder", _testDirectory } };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();

        services.AddSingleton(configuration);
        services.AddSingleton<IInstrumentRepository, InstrumentRepository>();
        var instrumentRepository = new InstrumentRepository(new NullLogger<InstrumentRepository>(), configuration);
        instrumentRepository.StartAsync().Wait();
        // --- 3. MarketDataManager의 의존성 등록 (Mock 또는 실제) ---
        // MarketDataManager는 SubscriptionConfig가 필요하므로, 테스트용으로 빈 Config를 등록
        services.AddSingleton(new SubscriptionConfig());
        // MarketDataDistributor는 Disruptor에 의존하므로 Mocking
        services.AddSingleton<MarketDataDistributor>();
        services.AddSingleton<IOrderRouter, OrderRouter>();
        services.AddSingleton<IOrderUpdateHandler, OrderUpdateDistributor>();
        services.AddSingleton<IOrderGateway, NullOrderGateway>();
        services.AddSingleton<IOrderGatewayRegistry, OrderGatewayRegistry>();
        services.AddSingleton<IMarketDataManager, MarketDataManager>();
        _mockFeedHandler = new Mock<IFeedHandler>();
        services.AddSingleton(_mockFeedHandler.Object);
        services.AddSingleton(provider =>
        {
            var disruptor = new Disruptor<MarketDataEventWrapper>(() => new MarketDataEventWrapper(), 1024);
            disruptor.HandleEventsWith(provider.GetRequiredService<MarketDataDistributor>());
            return disruptor;
        });

        services.AddSingleton(provider =>
        {
            var disruptor = new Disruptor<OrderStatusReportWrapper>(() => new OrderStatusReportWrapper(), 1024);
            disruptor.HandleEventsWith(provider.GetRequiredService<IOrderUpdateHandler>());
            return disruptor;
        });
        // --- 4. Quoting 파이프라인의 실제 구현체들 등록 ---
        services.AddSingleton<IQuoteValidator, DefaultQuoteValidator>();
        services.AddSingleton<IOrderFactory, OrderFactory>(); // IOrderFactory의 실제 구현체 필요
        services.AddSingleton<IQuoterFactory, QuoterFactory>();
        services.AddSingleton<IFairValueProviderFactory, FairValueProviderFactory>();
        services.AddSingleton<IQuotingInstanceFactory, QuotingInstanceFactory>();

        // --- 5. 테스트 대상인 QuotingInstanceManager 등록 ---
        services.AddSingleton<IQuotingInstanceManager, QuotingInstanceManager>();

        _serviceProvider = services.BuildServiceProvider();
        _manager = _serviceProvider.GetRequiredService<IQuotingInstanceManager>();
        _instrumentRepo = _serviceProvider.GetRequiredService<IInstrumentRepository>();

        var disruptor = _serviceProvider.GetRequiredService<Disruptor<MarketDataEventWrapper>>();
        disruptor.Start();
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
    public void UpdateParameters_WhenNoInstanceExists_ShouldDeployAndActivateNewInstance()
    {
        // Arrange
        var bitmexInstrument = _instrumentRepo.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX)!;
        var binanceInstrument = _instrumentRepo.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE)!;

        var parameters = new QuotingParameters(
            bitmexInstrument.InstrumentId,
            "test",
            FairValueModel.Midp,
            binanceInstrument.InstrumentId,
            10m,
            -10m,
            0.5m,
            Quantity.FromDecimal(100),
            1,
            QuoterType.Log,
            true,
            Quantity.FromDecimal(300),
            Quantity.FromDecimal(300),
            HittingLogic.AllowAll
        );

        // Act
        _manager.UpdateInstanceParameters(parameters);

        // Assert
        var instance = _manager.GetInstance(bitmexInstrument.InstrumentId);
        instance.Should().NotBeNull();
        instance!.IsActive.Should().BeFalse("because a new instance should not be activated on first update.");

        var engine = instance.Engine;
        engine.CurrentParameters.Should().Be(parameters);
        engine.IsActive.Should().BeFalse();
    }

    [Test]
    public void UpdateParameters_WithTunableChanges_ShouldUpdateInPlaceAndActivate()
    {
        // Arrange
        var bitmexInstrument = _instrumentRepo.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX)!;
        var binanceInstrument = _instrumentRepo.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE)!;

        var initialParams = new QuotingParameters(
            bitmexInstrument.InstrumentId, "test", FairValueModel.Midp, binanceInstrument.InstrumentId,
            10m, -10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log, true, Quantity.FromDecimal(300),
            Quantity.FromDecimal(300), HittingLogic.AllowAll);

        _manager.UpdateInstanceParameters(initialParams);
        _manager.RetireInstance(initialParams.InstrumentId);

        var instanceBeforeUpdate = _manager.GetInstance(bitmexInstrument.InstrumentId);
        instanceBeforeUpdate!.IsActive.Should().BeFalse();

        // Spread, Skew, Size만 변경된 새로운 파라미터
        var updatedParams = new QuotingParameters(
            bitmexInstrument.InstrumentId, "test", FairValueModel.Midp, binanceInstrument.InstrumentId,
            20m, -20m, 1.0m, Quantity.FromDecimal(200), 1, QuoterType.Log, true, Quantity.FromDecimal(300),
            Quantity.FromDecimal(300), HittingLogic.AllowAll);

        // Act
        _manager.UpdateInstanceParameters(updatedParams);

        // Assert
        var instanceAfterUpdate = _manager.GetInstance(bitmexInstrument.InstrumentId);
        var engineAfterUpdate = instanceAfterUpdate!.Engine;

        instanceAfterUpdate.Should().BeSameAs(instanceBeforeUpdate, "because only tunable parameters changed.");

        engineAfterUpdate.CurrentParameters.Should().Be(updatedParams);
        engineAfterUpdate.IsActive.Should().BeFalse("because updating parameters should activate a paused instance with the same parameters twice.");
    }

    [Test]
    public void UpdateParameters_WithCoreParameterChange_ShouldCreateNewInstance()
    {
        // Arrange
        var bitmexInstrument = _instrumentRepo.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX)!;
        var binanceInstrument = _instrumentRepo.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE)!;

        var initialParams = new QuotingParameters(
            bitmexInstrument.InstrumentId, "test", FairValueModel.Midp, binanceInstrument.InstrumentId,
            10m, -10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log, true, Quantity.FromDecimal(300),
            Quantity.FromDecimal(300), HittingLogic.AllowAll);

        _manager.UpdateInstanceParameters(initialParams);
        var instanceBefore = _manager.GetInstance(bitmexInstrument.InstrumentId);

        // 핵심 파라미터인 FvModel 변경
        var newParams = new QuotingParameters(
            bitmexInstrument.InstrumentId, "test", FairValueModel.BestMidp, binanceInstrument.InstrumentId,
            10m, -10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log, true, Quantity.FromDecimal(300),
            Quantity.FromDecimal(300), HittingLogic.AllowAll);

        // Act
        _manager.UpdateInstanceParameters(newParams);

        // Assert
        var instanceAfter = _manager.GetInstance(bitmexInstrument.InstrumentId);
        instanceAfter.Should().NotBeNull();

        // 인스턴스가 재배포되었으므로, 이전 인스턴스와 다른 객체여야 함
        instanceAfter.Should().NotBeSameAs(instanceBefore);

        instanceAfter!.IsActive.Should().BeFalse();
        instanceAfter.Engine.CurrentParameters.FvModel.Should().Be(FairValueModel.BestMidp);
    }

    [Test]
    public void RetireInstance_ShouldDeactivateEngine()
    {
        // Arrange
        var bitmexInstrument = _instrumentRepo.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX)!;
        var parameters = new QuotingParameters(
            bitmexInstrument.InstrumentId, "test", FairValueModel.Midp, 2,
            10m, -10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log, true, Quantity.FromDecimal(300),
            Quantity.FromDecimal(300), HittingLogic.AllowAll);

        _manager.UpdateInstanceParameters(parameters);
        var instance = _manager.GetInstance(bitmexInstrument.InstrumentId);
        instance!.Activate(); // 명시적으로 활성화
        instance.IsActive.Should().BeTrue();

        // Act
        var result = _manager.RetireInstance(bitmexInstrument.InstrumentId);

        // Assert
        result.Should().NotBeNull();
        instance.IsActive.Should().BeFalse("because the instance should be deactivated by RetireInstance.");
    }

    [Test]
    public void OnConnectionLost_ShouldDeactivateRelatedInstancesOnly()
    {
        // --- Arrange ---
        // 1. 테스트에 사용할 Instrument 객체들을 가져옵니다.
        var bitmexInstrument = _instrumentRepo.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX)!;
        var binanceInstrument = _instrumentRepo.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE)!;

        // 2. BitMEX와 Binance에 대한 두 개의 다른 전략 파라미터를 정의합니다.
        var bitmexParams = new QuotingParameters(
            bitmexInstrument.InstrumentId, "test", FairValueModel.Midp, binanceInstrument.InstrumentId,
            10m, -10m, 0m, Quantity.FromDecimal(100), 1, QuoterType.Log, true, Quantity.FromDecimal(300),
            Quantity.FromDecimal(300), HittingLogic.AllowAll);

        var binanceParams = new QuotingParameters(
            binanceInstrument.InstrumentId, "test", FairValueModel.Midp, bitmexInstrument.InstrumentId,
            5m, -5m, 0m, Quantity.FromDecimal(1), 1, QuoterType.Log, true, Quantity.FromDecimal(300),
            Quantity.FromDecimal(300), HittingLogic.AllowAll);

        // 3. 두 전략을 배포하고 2번 활성화합니다.
        _manager.UpdateInstanceParameters(bitmexParams);
        _manager.UpdateInstanceParameters(bitmexParams);
        _manager.UpdateInstanceParameters(binanceParams);
        _manager.UpdateInstanceParameters(binanceParams);

        var bitmexInstance = _manager.GetInstance(bitmexInstrument.InstrumentId);
        var binanceInstance = _manager.GetInstance(binanceInstrument.InstrumentId);

        // 테스트 시작 전, 두 인스턴스가 모두 활성 상태인지 확인합니다.
        bitmexInstance!.IsActive.Should().BeTrue();
        binanceInstance!.IsActive.Should().BeTrue();

        // --- Act ---
        // 4. IFeedHandler의 Mock을 사용하여 "BitMEX 연결 끊김" 이벤트를 수동으로 발생시킵니다.
        TestContext.WriteLine("Simulating connection loss for BITMEX...");
        _mockFeedHandler.Raise(
            handler => handler.AdapterConnectionStateChanged += null,
            this, // sender (can be anything)
            new ConnectionStateChangedEventArgs(false, ExchangeEnum.BITMEX, "Simulated connection drop")
        );

        // 이벤트 핸들러가 비동기적으로 동작할 수 있으므로, 처리를 위해 잠시 대기합니다.
        // 실제로는 더 정교한 동기화 메커니즘을 사용할 수 있습니다.
        Task.Delay(100).Wait();

        // --- Assert ---
        // 5. QuotingInstanceManager가 올바르게 반응했는지 검증합니다.

        // BitMEX 인스턴스는 비활성화(Deactivated)되었어야 합니다.
        bitmexInstance.IsActive.Should().BeFalse(
            "because the BitMEX instance should be deactivated upon its exchange's connection loss.");

        // Binance 인스턴스는 영향을 받지 않고 여전히 활성 상태여야 합니다.
        binanceInstance.IsActive.Should().BeTrue(
            "because the Binance instance should not be affected by a BitMEX connection loss.");

        TestContext.WriteLine($"Validation successful: BitMEX instance IsActive={bitmexInstance.IsActive}, Binance instance IsActive={binanceInstance.IsActive}");
    }

    [Test]
    public void OnConnectionRestored_ShouldRedeployAndActivatePreviouslyRetiredInstances()
    {
        // --- Arrange ---
        // 1. Setup instruments and parameters for a BITMEX instance.
        var bitmexInstrument = _instrumentRepo.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX)!;
        var binanceInstrument = _instrumentRepo.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE)!;

        var bitmexParams = new QuotingParameters(
            bitmexInstrument.InstrumentId, "test", FairValueModel.Midp, binanceInstrument.InstrumentId,
            10m, -10m, 0m, Quantity.FromDecimal(100), 1, QuoterType.Log, true, Quantity.FromDecimal(300),
            Quantity.FromDecimal(300), HittingLogic.AllowAll);

        // 2. Deploy and activate the instance.
        _manager.UpdateInstanceParameters(bitmexParams); // Deploys
        _manager.UpdateInstanceParameters(bitmexParams); // Activates
        var instance = _manager.GetInstance(bitmexInstrument.InstrumentId);
        instance!.IsActive.Should().BeTrue("instance should be active initially.");

        // 3. Simulate a connection loss for BITMEX, which should retire the instance.
        TestContext.WriteLine("Step 1: Simulating connection loss for BITMEX...");
        _mockFeedHandler.Raise(
            handler => handler.AdapterConnectionStateChanged += null,
            this,
            new ConnectionStateChangedEventArgs(false, ExchangeEnum.BITMEX, "Simulated connection drop")
        );
        Task.Delay(100).Wait(); // Allow time for the event to be processed.

        // Verify that the instance is now inactive.
        instance.IsActive.Should().BeFalse("instance should be retired after connection loss.");
        TestContext.WriteLine($"Instance for {bitmexInstrument.Symbol} is now inactive as expected.");

        // --- Act ---
        // 4. Simulate the connection being restored for BITMEX.
        TestContext.WriteLine("Step 2: Simulating connection restoration for BITMEX...");
        _mockFeedHandler.Raise(
            handler => handler.AdapterConnectionStateChanged += null,
            this,
            new ConnectionStateChangedEventArgs(true, ExchangeEnum.BITMEX, "Connection restored")
        );

        // The redeployment logic includes a Task.Delay, so we need to wait longer.
        // Let's wait for a reasonable time for the async redeployment to complete.
        Task.Delay(TimeSpan.FromSeconds(6)).Wait(); // Wait for more than the 5s delay in RedeployRetiredInstancesAsync.

        // --- Assert ---
        // 5. Verify the instance has been redeployed and is now active.
        var redeployedInstance = _manager.GetInstance(bitmexInstrument.InstrumentId);
        redeployedInstance.Should().NotBeNull("the instance should exist after redeployment.");

        // The instance might be a new object after redeployment, so we check its properties.
        redeployedInstance!.IsActive.Should().BeTrue(
            "the instance should be automatically activated after its exchange's connection is restored.");

        redeployedInstance.CurrentParameters.Should().BeEquivalentTo(bitmexParams,
            "the redeployed instance should have the same parameters as before.");

        TestContext.WriteLine($"Validation successful: Instance for {bitmexInstrument.Symbol} is active again after reconnection.");
    }
}