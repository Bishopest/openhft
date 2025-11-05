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

namespace OpenHFT.Tests.Processing;

[TestFixture, Category("Integration")]
public class QuotingInstanceManagerTests_Integration
{
    private ServiceProvider _serviceProvider;
    private IQuotingInstanceManager _manager;
    private IInstrumentRepository _instrumentRepo;
    private string _testDirectory;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();

        // --- 1. 기본 서비스 등록 ---
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

        // --- 2. InstrumentRepository 설정 (요청사항 반영) ---
        _testDirectory = Path.Combine(Path.GetTempPath(), "QuotingInstanceManagerTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BITMEX,XBTUSD,PerpetualFuture,XBT,USD,0.5,1,1,1
BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.01,0.001,1,0.001";
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
        services.AddSingleton<MarketDataManager>();
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
            FairValueModel.Midp,
            binanceInstrument.InstrumentId,
            10m,
            0.5m,
            Quantity.FromDecimal(100),
            1,
            QuoterType.Log
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
            bitmexInstrument.InstrumentId, FairValueModel.Midp, binanceInstrument.InstrumentId,
            10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log);

        _manager.UpdateInstanceParameters(initialParams);
        _manager.RetireInstance(initialParams.InstrumentId);

        var instanceBeforeUpdate = _manager.GetInstance(bitmexInstrument.InstrumentId);
        instanceBeforeUpdate!.IsActive.Should().BeFalse();

        // Spread, Skew, Size만 변경된 새로운 파라미터
        var updatedParams = new QuotingParameters(
            bitmexInstrument.InstrumentId, FairValueModel.Midp, binanceInstrument.InstrumentId,
            20m, 1.0m, Quantity.FromDecimal(200), 1, QuoterType.Log);

        // Act
        _manager.UpdateInstanceParameters(updatedParams);

        // Assert
        var instanceAfterUpdate = _manager.GetInstance(bitmexInstrument.InstrumentId);
        var engineAfterUpdate = instanceAfterUpdate!.Engine;

        instanceAfterUpdate.Should().BeSameAs(instanceBeforeUpdate, "because only tunable parameters changed.");

        engineAfterUpdate.CurrentParameters.Should().Be(updatedParams);
        engineAfterUpdate.IsActive.Should().BeTrue("because updating parameters should activate a paused instance.");
    }

    [Test]
    public void UpdateParameters_WithCoreParameterChange_ShouldCreateNewInstance()
    {
        // Arrange
        var bitmexInstrument = _instrumentRepo.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX)!;
        var binanceInstrument = _instrumentRepo.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE)!;

        var initialParams = new QuotingParameters(
            bitmexInstrument.InstrumentId, FairValueModel.Midp, binanceInstrument.InstrumentId,
            10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log);

        _manager.UpdateInstanceParameters(initialParams);
        var instanceBefore = _manager.GetInstance(bitmexInstrument.InstrumentId);

        // 핵심 파라미터인 FvModel 변경
        var newParams = new QuotingParameters(
            bitmexInstrument.InstrumentId, FairValueModel.BestMidp, binanceInstrument.InstrumentId,
            10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log);

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
            bitmexInstrument.InstrumentId, FairValueModel.Midp, 2,
            10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log);

        _manager.UpdateInstanceParameters(parameters);
        var instance = _manager.GetInstance(bitmexInstrument.InstrumentId);
        instance!.Activate(); // 명시적으로 활성화
        instance.IsActive.Should().BeTrue();

        // Act
        var result = _manager.RetireInstance(bitmexInstrument.InstrumentId);

        // Assert
        result.Should().BeTrue();
        instance.IsActive.Should().BeFalse("because the instance should be deactivated by RetireInstance.");
    }
}