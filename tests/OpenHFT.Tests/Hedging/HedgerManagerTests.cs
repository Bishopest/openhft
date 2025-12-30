using System;
using Disruptor.Dsl;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Feed;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Gateway;
using OpenHFT.Hedging;
using OpenHFT.Processing;
using OpenHFT.Quoting;
using OpenHFT.Quoting.FairValue;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Validators;

namespace OpenHFT.Tests.Hedging;

[TestFixture]
public class HedgerManagerTests
{
    private ServiceProvider _serviceProvider;
    private IHedgerManager _manager;
    private IInstrumentRepository _instrumentRepo;
    private string _testDirectory;
    private Mock<IFeedHandler> _mockFeedHandler;
    private IQuotingInstanceManager _quotingManager;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();

        // --- 1. 기본 서비스 등록 ---
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

        // --- 2. InstrumentRepository 설정 (요청사항 반영) ---
        _testDirectory = Path.Combine(Path.GetTempPath(), "HedgerManagerTests", Guid.NewGuid().ToString());
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
        services.AddSingleton<IClientIdGenerator, ClientIdGenerator>();
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
        services.AddSingleton<IHedgerManager, HedgerManager>();
        services.AddSingleton<IQuotingInstanceManager, QuotingInstanceManager>();
        services.AddSingleton<IFxRateService, FxRateManager>();

        _serviceProvider = services.BuildServiceProvider();

        _quotingManager = _serviceProvider.GetRequiredService<IQuotingInstanceManager>();
        _manager = _serviceProvider.GetRequiredService<IHedgerManager>();
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
    public void OnConnectionLost_ShouldDeactivateRelatedHedger()
    {
        // --- Arrange ---
        // 1. 테스트에 사용할 Instrument 객체들을 가져옵니다.
        var bitmexInstrument = _instrumentRepo.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX)!;
        var binanceInstrument = _instrumentRepo.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE)!;

        // 2. BitMEX와 Binance에 대한 두 개의 다른 전략 파라미터를 정의합니다.
        var bitmexParams = new QuotingParameters(
            bitmexInstrument.InstrumentId, "test", FairValueModel.Midp, binanceInstrument.InstrumentId,
            10m, -10m, 0m, Quantity.FromDecimal(100), 1, QuoterType.Log, QuoterType.Log, true, Quantity.FromDecimal(300),
            Quantity.FromDecimal(300), HittingLogic.AllowAll);

        var binanceParams = new QuotingParameters(
            binanceInstrument.InstrumentId, "test", FairValueModel.Midp, bitmexInstrument.InstrumentId,
            5m, -5m, 0m, Quantity.FromDecimal(1), 1, QuoterType.Log, QuoterType.Log, true, Quantity.FromDecimal(300),
            Quantity.FromDecimal(300), HittingLogic.AllowAll);

        // 3. 두 전략을 배포하고 2번 활성화합니다.
        _quotingManager.UpdateInstanceParameters(bitmexParams);
        _quotingManager.UpdateInstanceParameters(bitmexParams);
        _quotingManager.UpdateInstanceParameters(binanceParams);
        _quotingManager.UpdateInstanceParameters(binanceParams);

        var bitmexInstance = _quotingManager.GetInstance(bitmexInstrument.InstrumentId);
        var binanceInstance = _quotingManager.GetInstance(binanceInstrument.InstrumentId);

        var bitmexHedgeParams = new HedgingParameters(bitmexInstrument.InstrumentId, binanceInstrument.InstrumentId, HedgeOrderType.OppositeFirst, Quantity.FromDecimal(100m));
        var binanceHedgeParams = new HedgingParameters(binanceInstrument.InstrumentId, bitmexInstrument.InstrumentId, HedgeOrderType.OppositeFirst, Quantity.FromDecimal(100m));

        // 4. 헤저 스타트
        _manager.UpdateHedgingParameters(bitmexHedgeParams);
        _manager.UpdateHedgingParameters(binanceHedgeParams);


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
        var bitmexHedger = _manager.GetHedger(bitmexHedgeParams.QuotingInstrumentId);
        var binanceHedger = _manager.GetHedger(binanceHedgeParams.QuotingInstrumentId);

        bitmexHedger.IsActive.Should().BeTrue(
            "because the BitMEX hedger(by Binance) should not be affected by a Bitmex connection loss.");

        // Binance 인스턴스는 영향을 받지 않고 여전히 활성 상태여야 합니다.
        binanceHedger.IsActive.Should().BeFalse(
            "because the Binance hedger(by BitMEX) should  be affected by a BitMEX connection loss.");

        // --- Act ---
        // 6. IFeedHandler의 Mock을 사용하여 "Bitmex 재연결" 이벤트를 수동으로 발생시킵니다.
        TestContext.WriteLine("Simulating connection reconstruction for BITMEX...");
        _mockFeedHandler.Raise(
                    handler => handler.AdapterConnectionStateChanged += null,
                    this, // sender (can be anything)
                    new ConnectionStateChangedEventArgs(true, ExchangeEnum.BITMEX, "Simulated connection reconstruction")
                );
        binanceHedger = _manager.GetHedger(binanceHedgeParams.QuotingInstrumentId);
        binanceHedger.IsActive.Should().BeTrue("because the Binance hedger should be re activated upon its hedge exchange(BitMEX)'s connection reconstruction.");
        TestContext.WriteLine($"Validation successful: Binance hedger IsActive={binanceHedger.IsActive}");
    }
}
