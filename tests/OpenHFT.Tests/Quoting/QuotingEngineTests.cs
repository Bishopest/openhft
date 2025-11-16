using System.Net.WebSockets;
using Disruptor.Dsl;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestPlatform.Utilities;
using Moq;
using NUnit.Framework;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Feed;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;
using OpenHFT.Gateway;
using OpenHFT.Processing;
using OpenHFT.Quoting;
using OpenHFT.Quoting.FairValue;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;
using OpenHFT.Quoting.Validators;
using OpenHFT.Tests.Processing;

namespace OpenHFT.Tests.Quoting;

[TestFixture]
public class QuotingEngineTests
{
    private ServiceProvider _serviceProvider;
    private IQuotingEngine _engine;
    private LogQuoter _bidQuoter;
    private LogQuoter _askQuoter;
    private IInstrumentRepository _instrumentRepo;
    private MarketDataManager _marketDataManager;
    private MockAdapter _mockBinanceAdapter; // For FV source data
    private IOrderRouter _orderRouter;
    private IFeedHandler _feedHandler;
    private string _testDirectory;
    private Instrument _xbtusd;
    private Instrument _btcusdt;


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

        _mockBinanceAdapter = new MockAdapter(new NullLogger<MockAdapter>(), ProductType.PerpetualFuture, instrumentRepository, ExecutionMode.Testnet);
        services.AddSingleton<IFeedAdapter>(_mockBinanceAdapter);
        services.AddSingleton<IFeedAdapterRegistry, FeedAdapterRegistry>();
        services.AddSingleton<IFeedHandler, FeedHandler>();
        services.AddSingleton<MarketDataDistributor>();
        services.AddSingleton<MarketDataManager>();
        services.AddSingleton<IOrderRouter, OrderRouter>();
        services.AddSingleton<IOrderUpdateHandler, OrderUpdateDistributor>();
        services.AddSingleton<IOrderGateway, NullOrderGateway>();
        services.AddSingleton<IOrderGatewayRegistry, OrderGatewayRegistry>();
        services.AddSingleton<IQuoteValidator, DefaultQuoteValidator>();
        services.AddSingleton<IOrderFactory, OrderFactory>();
        services.AddSingleton<IQuoterFactory, QuoterFactory>();
        services.AddSingleton<IFairValueProviderFactory, FairValueProviderFactory>();
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

        // --- 5. ServiceProvider 빌드 및 객체 생성 ---
        _serviceProvider = services.BuildServiceProvider();
        _marketDataManager = _serviceProvider.GetRequiredService<MarketDataManager>();
        _feedHandler = _serviceProvider.GetRequiredService<IFeedHandler>();
        _orderRouter = _serviceProvider.GetRequiredService<IOrderRouter>();

        // QuotingEngine을 직접 생성
        var quoterFactory = _serviceProvider.GetRequiredService<IQuoterFactory>();
        _xbtusd = instrumentRepository.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX)!;
        _btcusdt = instrumentRepository.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE)!;
        _marketDataManager.Install(_xbtusd);
        _marketDataManager.Install(_btcusdt);

        _bidQuoter = (LogQuoter)quoterFactory.CreateQuoter(_xbtusd, Side.Buy, QuoterType.Log);
        _askQuoter = (LogQuoter)quoterFactory.CreateQuoter(_xbtusd, Side.Sell, QuoterType.Log);

        var marketMaker = new MarketMaker(
            _serviceProvider.GetRequiredService<ILogger<MarketMaker>>(),
            _xbtusd, _bidQuoter, _askQuoter,
            _serviceProvider.GetRequiredService<IQuoteValidator>()
        );

        var fvProvider = _serviceProvider.GetRequiredService<IFairValueProviderFactory>()
            .CreateProvider(FairValueModel.Midp, _btcusdt.InstrumentId);

        var parameters = new QuotingParameters(
            _xbtusd.InstrumentId,
            FairValueModel.Midp,
            _btcusdt.InstrumentId,
            10m,
            -10m,
            0.5m,
            Quantity.FromDecimal(100),
            1,
            QuoterType.Log,
            true
        );
        _engine = new QuotingEngine(
            _serviceProvider.GetRequiredService<ILogger<QuotingEngine>>(),
            _xbtusd,
            marketMaker,
            fvProvider,
            parameters,
            _marketDataManager
        );

        // Factory에서 수행하던 이벤트 연결을 수동으로 수행
        marketMaker.OrderFullyFilled += () => _engine.PauseQuoting(TimeSpan.FromSeconds(3));
        var disruptor = _serviceProvider.GetRequiredService<Disruptor<MarketDataEventWrapper>>();
        disruptor.Start();
    }

    [Test]
    public async Task When_OrderIsFullyFilled_ShouldPauseQuoting_AndThenResume()
    {
        // --- Arrange ---
        _engine.Start();
        _engine.Activate();

        // 1. 첫 번째 호가를 생성시키기 위해 시장 데이터 주입
        var testUpdates1 = new PriceLevelEntryArray();
        testUpdates1[0] = new PriceLevelEntry(Side.Buy, 50000m, 100m);
        testUpdates1[1] = new PriceLevelEntry(Side.Sell, 3000m, 200m);

        var btcEvent1 = new MarketDataEvent(1, 1, EventKind.Snapshot, _btcusdt.InstrumentId, _btcusdt.SourceExchange, 0, BinanceTopic.DepthUpdate.TopicId, 2, testUpdates1);
        _mockBinanceAdapter.FireMarketDataEvent(btcEvent1);
        await Task.Delay(200); // 처리 대기

        // 첫 번째 호가가 LogQuoter에 기록되었는지 확인
        _bidQuoter.LatestQuote.Should().NotBeNull();
        var initialQuotePrice = _bidQuoter.LatestQuote.Value.Price;
        // --- Act 1: 전량 체결 시뮬레이션 ---
        TestContext.WriteLine("Simulating a full fill event...");

        // OrderRouter를 통해 'Filled' 상태의 OrderStatusReport를 직접 주입
        var fillReport = new OrderStatusReport(
            1234, "dummy-exo-id", null, _xbtusd.InstrumentId, Side.Buy,
            OrderStatus.Filled,
            initialQuotePrice,
            Quantity.FromDecimal(100),
            Quantity.FromDecimal(0), // LeavesQty = 0
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            lastQuantity: Quantity.FromDecimal(100) // LastQty
        );
        _orderRouter.RouteReport(fillReport);

        await Task.Delay(200); // 이벤트 전파 대기

        // --- Assert 1: 호가가 중지되었는지 확인 ---

        TestContext.WriteLine("Triggering another market data update while paused...");
        var testUpdates2 = new PriceLevelEntryArray();
        testUpdates2[0] = new PriceLevelEntry(Side.Buy, 50000m, 100m);
        testUpdates2[1] = new PriceLevelEntry(Side.Sell, 3000m, 200m);

        var btcEvent2 = new MarketDataEvent(1, 1, EventKind.Snapshot, _btcusdt.InstrumentId, _btcusdt.SourceExchange, 0, BinanceTopic.DepthUpdate.TopicId, 2, testUpdates2);
        _mockBinanceAdapter.FireMarketDataEvent(btcEvent2);
        await Task.Delay(200);

        _bidQuoter.LatestQuote.Value.Price.ToDecimal().Should().Be(initialQuotePrice.ToDecimal());
        TestContext.WriteLine(" -> Quoting is paused as expected.");

        // --- Act 2 & Assert 2: 쿨다운 후 호가 재개 확인 ---
        TestContext.WriteLine("Waiting for 3.5 seconds to ensure quoting resumes...");
        await Task.Delay(3500);

        var testUpdates3 = new PriceLevelEntryArray();
        testUpdates3[0] = new PriceLevelEntry(Side.Buy, 50000m, 100m);
        testUpdates3[1] = new PriceLevelEntry(Side.Sell, 30000m, 200m);

        var btcEvent3 = new MarketDataEvent(1, 1, EventKind.Snapshot, _btcusdt.InstrumentId, _btcusdt.SourceExchange, 0, BinanceTopic.DepthUpdate.TopicId, 2, testUpdates3);
        _mockBinanceAdapter.FireMarketDataEvent(btcEvent3);
        await Task.Delay(200);

        _bidQuoter.LatestQuote.Value.Price.ToDecimal().Should().NotBe(initialQuotePrice.ToDecimal());
        TestContext.WriteLine(" -> Quoting has resumed as expected.");
    }
}

public class MockAdapter : BaseFeedAdapter
{
    public MockAdapter(ILogger logger, ProductType type, IInstrumentRepository instrumentRepository, ExecutionMode executionMode) : base(logger, type, instrumentRepository, executionMode)
    {
    }

    public override ExchangeEnum SourceExchange => ExchangeEnum.BINANCE;

    protected override void ConfigureWebsocket(ClientWebSocket websocket)
    {
        throw new NotImplementedException();
    }

    protected override string GetBaseUrl()
    {
        throw new NotImplementedException();
    }

    protected override Task ProcessMessage(MemoryStream messageStream)
    {
        throw new NotImplementedException();
    }

    public void FireMarketDataEvent(MarketDataEvent marketDataEvent)
    {
        // BaseFeedAdapter의 protected virtual 메서드를 호출하여 이벤트를 발생시킵니다.
        base.OnMarketDataReceived(marketDataEvent);
    }
    public void FireOrderUpdateEvent(OrderStatusReport report)
    {
        base.OnOrderUpdateReceived(report);
    }
    protected override string? GetPingMessage()
    {
        throw new NotImplementedException();
    }

    protected override bool IsPongMessage(MemoryStream messageStream)
    {
        throw new NotImplementedException();
    }

    protected override Task DoSubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    protected override Task DoUnsubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}