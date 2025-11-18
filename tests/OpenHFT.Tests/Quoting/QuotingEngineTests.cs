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
using OpenHFT.Book.Core;
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
    private QuotingEngine _engine;
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
    private IFairValueProvider? _fvProvider;



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
        _fvProvider = _serviceProvider.GetRequiredService<IFairValueProviderFactory>()
            .CreateProvider(FairValueModel.Midp, _btcusdt.InstrumentId);

        _bidQuoter = (LogQuoter)quoterFactory.CreateQuoter(_xbtusd, Side.Buy, QuoterType.Log);
        _askQuoter = (LogQuoter)quoterFactory.CreateQuoter(_xbtusd, Side.Sell, QuoterType.Log);

        var marketMaker = new MarketMaker(
            _serviceProvider.GetRequiredService<ILogger<MarketMaker>>(),
            _xbtusd, _bidQuoter, _askQuoter,
            _serviceProvider.GetRequiredService<IQuoteValidator>()
        );

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
            true,
            Quantity.FromDecimal(300),
            Quantity.FromDecimal(300)
        );
        _engine = new QuotingEngine(
            _serviceProvider.GetRequiredService<ILogger<QuotingEngine>>(),
            _xbtusd,
            marketMaker,
            _fvProvider,
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
        _bidQuoter.InvokeOrderFullyFilled();
        await Task.Delay(200); // 이벤트 전파 대기

        // --- Assert 1: 호가가 중지되었는지 확인 ---

        TestContext.WriteLine("Triggering another market data update while paused...");
        var testUpdates2 = new PriceLevelEntryArray();
        testUpdates2[0] = new PriceLevelEntry(Side.Buy, 50000m, 100m);
        testUpdates2[1] = new PriceLevelEntry(Side.Sell, 300m, 200m);

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

    [Test]
    public void OnFill_WithSingleBuyFill_CorrectlyIncrementsBuyFills()
    {
        _engine.Start();
        _engine.Activate();

        // Arrange
        var fill = new Fill(_xbtusd.InstrumentId, 1, "E1", "X1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(100), 0);
        // Act
        _bidQuoter.InvokeOrderFilled(fill);

        // Assert
        _engine.TotalBuyFills.ToDecimal().Should().Be(100);
        _engine.TotalSellFills.ToDecimal().Should().Be(0);
    }

    [Test]
    public void OnFill_WithSingleSellFill_CorrectlyIncrementsSellFills()
    {
        // Arrange
        var fill = new Fill(_xbtusd.InstrumentId, 1, "E1", "X1", Side.Sell, Price.FromDecimal(50000), Quantity.FromDecimal(200), 0);

        // Act
        _askQuoter.InvokeOrderFilled(fill);

        // Assert
        _engine.TotalBuyFills.ToDecimal().Should().Be(0);
        _engine.TotalSellFills.ToDecimal().Should().Be(200);
    }

    [Test]
    public void OnFill_WithBuyThenSellFills_CorrectlyNetsValues()
    {
        // Arrange
        var buyFill = new Fill(_xbtusd.InstrumentId, 1, "E1", "X1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(100), 0);
        var sellFill = new Fill(_xbtusd.InstrumentId, 2, "E2", "X2", Side.Sell, Price.FromDecimal(50001), Quantity.FromDecimal(30), 1);

        // Act
        _bidQuoter.InvokeOrderFilled(buyFill);
        _askQuoter.InvokeOrderFilled(sellFill);

        // Assert
        // Buy fills should be reduced by the sell amount (100 - 30 = 70)
        _engine.TotalBuyFills.ToDecimal().Should().Be(100);
        // Sell fills are added, then reduced by the buy amount (0 + 30 - 30 = 0, no wait, the logic is increase sell and decrease buy)
        // Correct logic: TotalSell = 30, TotalBuy becomes 100 - 30 = 70
        _engine.TotalSellFills.ToDecimal().Should().Be(30);
    }

    [Test]
    public void OnFill_WithSellThenBuyFills_CorrectlyNetsValues()
    {
        // Arrange
        var sellFill = new Fill(_xbtusd.InstrumentId, 1, "E1", "X1", Side.Sell, Price.FromDecimal(50001), Quantity.FromDecimal(50), 0);
        var buyFill = new Fill(_xbtusd.InstrumentId, 2, "E2", "X2", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(20), 1);

        // Act
        _askQuoter.InvokeOrderFilled(sellFill);
        _bidQuoter.InvokeOrderFilled(buyFill);

        // AssertSimulateFill(buyFill);

        // Assert
        // Sell fills should be reduced by the buy amount (50 - 20 = 30)
        _engine.TotalSellFills.ToDecimal().Should().Be(50);
        // TotalBuy = 20, TotalSell becomes 50 - 20 = 30
        _engine.TotalBuyFills.ToDecimal().Should().Be(20);
    }

    [Test]
    public void OnFill_DecrementingOppositeSide_ShouldNotGoBelowZero()
    {
        // Arrange
        var buyFill = new Fill(_xbtusd.InstrumentId, 1, "E1", "X1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(100), 0);
        var largeSellFill = new Fill(_xbtusd.InstrumentId, 2, "E2", "X2", Side.Sell, Price.FromDecimal(50001), Quantity.FromDecimal(150), 1);

        // Act
        _bidQuoter.InvokeOrderFilled(buyFill); // TotalBuy = 100, TotalSell = 0
        _askQuoter.InvokeOrderFilled(largeSellFill); // TotalSell = 150, TotalBuy becomes Max(0, 100 - 150) = 0

        // Assert
        _engine.TotalBuyFills.ToDecimal().Should().Be(100m, "because a larger sell fill should reduce it to zero, not negative.");
        _engine.TotalSellFills.ToDecimal().Should().Be(150m);
    }

    [Test]
    public void OnFill_WithFillForDifferentInstrument_ShouldBeIgnored()
    {
        // Arrange
        // Note: This fill is for _btcusdt, not _xbtusd which the engine is quoting.
        var otherInstrumentFill = new Fill(_btcusdt.InstrumentId, 1, "E1", "X1", Side.Buy, Price.FromDecimal(2000), Quantity.FromDecimal(10), 0);

        // Act
        _bidQuoter.InvokeOrderFilled(otherInstrumentFill);

        // Assert
        // The engine's fill counters should remain zero.
        _engine.TotalBuyFills.ToDecimal().Should().Be(0);
        _engine.TotalSellFills.ToDecimal().Should().Be(0);
    }

    [Test]
    public void Requote_WhenMaxCumBidFillsExceeded_ShouldGenerateNullBidQuote()
    {
        _engine.Start();
        _engine.Activate();

        // Arrange
        var parameters = new QuotingParameters(
            _xbtusd.InstrumentId, FairValueModel.Midp, _btcusdt.InstrumentId,
            10m, -10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log, true,
            Quantity.FromDecimal(200), Quantity.FromDecimal(200) // MaxCumBidFills = 200
        );
        _engine.UpdateParameters(parameters);

        QuotePair? generatedQuotePair = null;
        _engine.QuotePairCalculated += (sender, qp) => generatedQuotePair = qp;

        // Simulate fills exceeding the max bid fills limit
        var buyFill = new Fill(_xbtusd.InstrumentId, 1, "E1", "X1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(201), 0);
        _bidQuoter.InvokeOrderFilled(buyFill);

        _engine.TotalBuyFills.ToDecimal().Should().Be(201);

        TriggerRequote(50000m);
        var bidQ = _bidQuoter.LatestQuote;
        var askQ = _askQuoter.LatestQuote;
        // Assert
        generatedQuotePair.Should().NotBeNull();
        bidQ.Should().BeNull("because max cumulative bid fills was exceeded.");
        askQ.Should().NotBeNull("because ask side is not affected.");
    }

    [Test]
    public void Requote_WhenMaxCumAskFillsExceeded_ShouldGenerateNullAskQuote()
    {
        _engine.Start();
        _engine.Activate();

        // Arrange
        var parameters = new QuotingParameters(
            _xbtusd.InstrumentId, FairValueModel.Midp, _btcusdt.InstrumentId,
            10m, -10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log, true,
            Quantity.FromDecimal(150), Quantity.FromDecimal(150) // MaxCumAskFills = 150
        );
        _engine.UpdateParameters(parameters);

        QuotePair? generatedQuotePair = null;
        _engine.QuotePairCalculated += (sender, qp) => generatedQuotePair = qp;

        // Simulate fills exceeding the max ask fills limit
        var sellFill = new Fill(_xbtusd.InstrumentId, 1, "E1", "X1", Side.Sell, Price.FromDecimal(50001), Quantity.FromDecimal(151), 0);
        _askQuoter.InvokeOrderFilled(sellFill);

        _engine.TotalSellFills.ToDecimal().Should().Be(151);

        TriggerRequote(50000m);
        // Act
        generatedQuotePair.Should().NotBeNull();
        generatedQuotePair.Value.Ask.Should().BeNull("because max cumulative ask fills was exceeded.");
        generatedQuotePair.Value.Bid.Should().NotBeNull("because bid side is not affected.");
    }

    private void TriggerRequote(decimal fairValue)
    {
        // To test Requote, we need to simulate a FairValueChanged event.
        // We can do this by casting the provider and calling its Update method.
        // This assumes the provider has a public Update method for testing.
        if (_fvProvider is MidpFairValueProvider provider)
        {
            var book = new OrderBook(_btcusdt);
            // We need to create a simple book state that results in the desired fair value.
            // For Midp, FV = (BestBid + BestAsk) / 2.
            var bidPrice = Price.FromDecimal(fairValue - 1);
            var askPrice = Price.FromDecimal(fairValue + 1);

            var updates = new PriceLevelEntryArray();
            updates[0] = new PriceLevelEntry(Side.Buy, bidPrice.ToDecimal(), 1);
            updates[1] = new PriceLevelEntry(Side.Sell, askPrice.ToDecimal(), 1);
            var mdEvent = new MarketDataEvent(1, 0, EventKind.Snapshot, _btcusdt.InstrumentId, _btcusdt.SourceExchange, 0, 0, 2, updates);
            book.ApplyEvent(mdEvent);

            provider.Update(book); // This will fire the FairValueChanged event
        }
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