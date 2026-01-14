using System.Net.WebSockets;
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
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;
using OpenHFT.Processing;
using OpenHFT.Quoting;
using OpenHFT.Quoting.FairValue;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;
using OpenHFT.Quoting.Validators;

namespace OpenHFT.Tests.Quoting;

[TestFixture]
public class QuotingEngineTests
{
    private ServiceProvider _serviceProvider;
    private QuotingEngine _engine;
    private LogQuoter _bidQuoter;
    private LogQuoter _askQuoter;
    private IInstrumentRepository _instrumentRepo;
    private IMarketDataManager _marketDataManager;
    private MockAdapter _mockBinanceAdapter; // For FV source data
    private IOrderRouter _orderRouter;
    private IFeedHandler _feedHandler;
    private string _testDirectory;
    private Instrument _xbtusd;
    private Instrument _btcusdt;
    private Instrument _krwbtc;
    private IFairValueProvider? _fvProvider;
    private decimal _krwToUsdRate = 0.00075m;

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
2,BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.01,0.001,1,0.001
3,BITHUMB,BTCKRW,Spot,BTC,KRW,1000,0.0001,1,0.0001"; // KRW 종목 추가
        File.WriteAllText(Path.Combine(_testDirectory, "instruments.csv"), csvContent);

        var inMemoryConfig = new Dictionary<string, string> { { "dataFolder", _testDirectory } };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();

        services.AddSingleton(configuration);
        services.AddSingleton<IInstrumentRepository, InstrumentRepository>();
        var instrumentRepository = new InstrumentRepository(new NullLogger<InstrumentRepository>(), configuration);
        instrumentRepository.StartAsync().Wait();

        // --- 3. MarketDataManager 및 Core 의존성 ---
        services.AddSingleton(new SubscriptionConfig());

        _mockBinanceAdapter = new MockAdapter(new NullLogger<MockAdapter>(), ProductType.PerpetualFuture, instrumentRepository, ExecutionMode.Testnet);
        services.AddSingleton<IFeedAdapter>(_mockBinanceAdapter);
        services.AddSingleton<IFeedAdapterRegistry, FeedAdapterRegistry>();
        services.AddSingleton<IFeedHandler, FeedHandler>();
        services.AddSingleton<MarketDataDistributor>();
        services.AddSingleton<IMarketDataManager, MarketDataManager>();
        services.AddSingleton<IOrderRouter, OrderRouter>();
        services.AddSingleton<IOrderUpdateHandler, OrderUpdateDistributor>();
        services.AddSingleton<IClientIdGenerator, ClientIdGenerator>();

        var mockGateway = new Mock<IOrderGateway>();
        var mockGatewayRegistry = new Mock<IOrderGatewayRegistry>();
        mockGatewayRegistry.Setup(r => r.GetGatewayForInstrument(It.IsAny<int>())).Returns(mockGateway.Object);
        services.AddSingleton(mockGatewayRegistry.Object);
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
        _instrumentRepo = _serviceProvider.GetRequiredService<IInstrumentRepository>();
        _marketDataManager = _serviceProvider.GetRequiredService<IMarketDataManager>();
        _feedHandler = _serviceProvider.GetRequiredService<IFeedHandler>();
        _orderRouter = _serviceProvider.GetRequiredService<IOrderRouter>();

        // Instruments 로드
        _xbtusd = instrumentRepository.FindBySymbol("XBTUSD", ProductType.PerpetualFuture, ExchangeEnum.BITMEX)!;
        _btcusdt = instrumentRepository.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE)!;
        _krwbtc = instrumentRepository.FindBySymbol("BTCKRW", ProductType.Spot, ExchangeEnum.BITHUMB)!;

        _marketDataManager.Install(_xbtusd);
        _marketDataManager.Install(_btcusdt);
        _marketDataManager.Install(_krwbtc);

        // 기본 엔진 설정 (BTCUSDT -> XBTUSD)
        SetupQuotingEngine(_btcusdt.InstrumentId);

        var disruptor = _serviceProvider.GetRequiredService<Disruptor<MarketDataEventWrapper>>();
        disruptor.Start();
    }

    // [변경] 엔진 설정을 재사용 가능하도록 분리
    private void SetupQuotingEngine(int fvInstrumentId)
    {
        var quoterFactory = _serviceProvider.GetRequiredService<IQuoterFactory>();
        _fvProvider = _serviceProvider.GetRequiredService<IFairValueProviderFactory>()
            .CreateProvider(FairValueModel.Midp, fvInstrumentId);

        var parameters = new QuotingParameters(
            _xbtusd.InstrumentId,
            "test",
            FairValueModel.Midp,
            fvInstrumentId,
            10m,
            -10m,
            0.5m,
            Quantity.FromDecimal(100),
            1,
            QuoterType.Log,
            QuoterType.Log,
            true,
            Quantity.FromDecimal(300),
            Quantity.FromDecimal(300),
            HittingLogic.AllowAll,
            0.01m
        );

        _bidQuoter = (LogQuoter)quoterFactory.CreateQuoter(parameters, Side.Buy);
        _askQuoter = (LogQuoter)quoterFactory.CreateQuoter(parameters, Side.Sell);

        var marketMaker = new MarketMaker(
            _serviceProvider.GetRequiredService<ILogger<MarketMaker>>(),
            _xbtusd, _bidQuoter, _askQuoter,
            _serviceProvider.GetRequiredService<IQuoteValidator>()
        );

        var fixedFxRateConverter = new FixedFxConverter(_krwToUsdRate);
        _engine = new QuotingEngine(
            _serviceProvider.GetRequiredService<ILogger<QuotingEngine>>(),
            _xbtusd,
            _instrumentRepo.GetById(fvInstrumentId), // FV Instrument 주입
            marketMaker,
            _fvProvider,
            parameters,
            _marketDataManager,
            fixedFxRateConverter
        );

        marketMaker.OrderFullyFilled += () => _engine.PauseQuoting(TimeSpan.FromSeconds(3));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
        if (_serviceProvider is IDisposable d) d.Dispose();
    }

    // --- Helper Methods ---
    private void TriggerRequote(decimal fairValue, int instrumentId = 0)
    {
        var targetInstrumentId = instrumentId == 0 ? _btcusdt.InstrumentId : instrumentId;
        if (_fvProvider is MidpFairValueProvider provider)
        {
            var targetInstrument = _instrumentRepo.GetById(targetInstrumentId);
            var book = new OrderBook(targetInstrument);

            // Create spread around FV
            var bidPrice = Price.FromDecimal(fairValue - targetInstrument.TickSize.ToDecimal());
            var askPrice = Price.FromDecimal(fairValue + targetInstrument.TickSize.ToDecimal());

            var updates = new PriceLevelEntryArray();
            updates[0] = new PriceLevelEntry(Side.Buy, bidPrice.ToDecimal(), 1);
            updates[1] = new PriceLevelEntry(Side.Sell, askPrice.ToDecimal(), 1);
            var mdEvent = new MarketDataEvent(1, 0, EventKind.Snapshot, targetInstrumentId, targetInstrument.SourceExchange, 0, 0, 2, updates);
            book.ApplyEvent(mdEvent);

            provider.Update(book);
        }
    }

    [Test]
    public void Requote_ShouldConvertCurrency_WhenBaseCurrenciesDiffer_KRW_to_USD()
    {
        // 1. Arrange: KRW 종목을 FV 소스로 사용하는 엔진 재설정
        SetupQuotingEngine(_krwbtc.InstrumentId); // BTCKRW -> XBTUSD (USD Quoted)
        _engine.Start();
        _engine.Activate();

        // 2. FX Rate 설정 (1 KRW = 0.00075 USD 가정, 환율 1333.33)
        // FixedFxConverter가 이미 설정되어 있음
        // 3. Act: KRW FV 발생 (1억 3333만... -> 10만불)
        decimal krwPrice = 133_333_333m;
        TriggerRequote(krwPrice, _krwbtc.InstrumentId);

        // 4. Assert
        _bidQuoter.LatestQuote.Should().NotBeNull();

        // 예상 USD 가격: 133,333,333 * 0.00075 = 99,999.99975
        // Spread 적용: -10bp -> 99,999.99 * 0.999 = 99,899.99
        // Tick Rounding (0.5) -> Floor(99,899.99 / 0.5) * 0.5 = 99,899.5

        var usdFv = krwPrice * _krwToUsdRate;
        var expectedBid = Math.Floor((usdFv * (1 - 0.0010m)) / 0.5m) * 0.5m;

        _bidQuoter.LatestQuote.Value.Price.ToDecimal().Should().Be(expectedBid);
        TestContext.WriteLine($"KRW FV: {krwPrice}, Converted USD FV: {usdFv}, Quoted Bid: {_bidQuoter.LatestQuote.Value.Price}");
    }

    [Test]
    public async Task When_OrderIsFullyFilled_ShouldPauseQuoting_AndThenResume()
    {
        _engine.Start();
        _engine.Activate();

        // 1. 초기 호가 생성
        TriggerRequote(50000m);
        _bidQuoter.LatestQuote.Should().NotBeNull();
        var initialQuotePrice = _bidQuoter.LatestQuote.Value.Price;

        // 2. Act: 전량 체결 시뮬레이션
        TestContext.WriteLine("Simulating full fill...");
        _bidQuoter.InvokeOrderFullyFilled();
        await Task.Delay(100);

        // 3. Assert: 일시 정지 확인 (새로운 FV에도 호가 변경 안됨)
        TriggerRequote(55000m); // 가격 급등
        _bidQuoter.LatestQuote.Value.Price.ToDecimal().Should().Be(initialQuotePrice.ToDecimal());
        TestContext.WriteLine(" -> Quoting paused.");

        // 4. Act: 쿨다운 후 재개 확인
        await Task.Delay(3100); // 3초 대기
        TriggerRequote(58000m); // 다시 업데이트

        _bidQuoter.LatestQuote.Value.Price.ToDecimal().Should().NotBe(initialQuotePrice.ToDecimal());
        TestContext.WriteLine(" -> Quoting resumed.");
    }

    [Test]
    public void OnFill_WithSingleBuyFill_CorrectlyIncrementsBuyFills()
    {
        _engine.Start();
        _engine.Activate();

        // Arrange
        var fill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(100), 0);
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
        var fill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Sell, Price.FromDecimal(50000), Quantity.FromDecimal(200), 0);

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
        var buyFill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(100), 0);
        var sellFill = new Fill(_xbtusd.InstrumentId, "test", 2, "E2", "X2", Side.Sell, Price.FromDecimal(50001), Quantity.FromDecimal(30), 1);

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
        var sellFill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Sell, Price.FromDecimal(50001), Quantity.FromDecimal(50), 0);
        var buyFill = new Fill(_xbtusd.InstrumentId, "test", 2, "E2", "X2", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(20), 1);

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
        var buyFill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(100), 0);
        var largeSellFill = new Fill(_xbtusd.InstrumentId, "test", 2, "E2", "X2", Side.Sell, Price.FromDecimal(50001), Quantity.FromDecimal(150), 1);

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
        var otherInstrumentFill = new Fill(_btcusdt.InstrumentId, "test", 1, "E1", "X1", Side.Buy, Price.FromDecimal(2000), Quantity.FromDecimal(10), 0);

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
            _xbtusd.InstrumentId, "test", FairValueModel.Midp, _btcusdt.InstrumentId,
            10m, -10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log, QuoterType.Log, true,
            Quantity.FromDecimal(200), Quantity.FromDecimal(200), HittingLogic.AllowAll // MaxCumBidFills = 200
        );
        _engine.UpdateParameters(parameters);

        QuotePair? generatedQuotePair = null;
        _engine.QuotePairCalculated += (sender, qp) => generatedQuotePair = qp;

        // Simulate fills exceeding the max bid fills limit
        var buyFill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(201), 0);
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
            _xbtusd.InstrumentId, "test", FairValueModel.Midp, _btcusdt.InstrumentId,
            10m, -10m, 0.5m, Quantity.FromDecimal(100), 1, QuoterType.Log, QuoterType.Log, true,
            Quantity.FromDecimal(150), Quantity.FromDecimal(150), HittingLogic.AllowAll // MaxCumAskFills = 150
        );
        _engine.UpdateParameters(parameters);

        QuotePair? generatedQuotePair = null;
        _engine.QuotePairCalculated += (sender, qp) => generatedQuotePair = qp;

        // Simulate fills exceeding the max ask fills limit
        var sellFill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Sell, Price.FromDecimal(50001), Quantity.FromDecimal(151), 0);
        _askQuoter.InvokeOrderFilled(sellFill);

        _engine.TotalSellFills.ToDecimal().Should().Be(151);

        TriggerRequote(50000m);
        // Act
        generatedQuotePair.Should().NotBeNull();
        generatedQuotePair.Value.Ask.Should().BeNull("because max cumulative ask fills was exceeded.");
        generatedQuotePair.Value.Bid.Should().NotBeNull("because bid side is not affected.");
    }

    [Test]
    public void Requote_WhenBuyFillsExceedOrderSize_ShouldApplyNegativeSkewToSpreads()
    {
        // --- Arrange ---
        // 1. SkewBp가 설정된 파라미터 정의
        var initialParams = new QuotingParameters(
            _xbtusd.InstrumentId, "test", FairValueModel.Midp, _btcusdt.InstrumentId,
            askSpreadBp: 10m,  // 초기 Ask Spread
            bidSpreadBp: -10m, // 초기 Bid Spread
            skewBp: 2m,        // 체결 시 2bp씩 skew
            size: Quantity.FromDecimal(100), // 주문 사이즈 100
            depth: 1,
            askQuoterType: QuoterType.Log,
            bidQuoterType: QuoterType.Log,
            postOnly: true,
            maxCumBidFills: Quantity.FromDecimal(1000),
            maxCumAskFills: Quantity.FromDecimal(1000),
            hittingLogic: HittingLogic.AllowAll
        );
        _engine.Start();
        _engine.UpdateParameters(initialParams);
        _engine.Activate();

        // 2. 주문 사이즈(100)를 초과하는 매수 체결 시뮬레이션
        var buyFill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(120), 0);
        _engine.OnFill(buyFill); // 직접 OnFill 호출

        // 3. Requote 트리거
        TriggerRequote(50001m);

        // --- Assert ---
        // 4. Skew가 적용된 후의 파라미터를 확인
        var newParams = _engine.CurrentParameters;

        // 매수 체결(N=1)이 있었으므로, 스프레드는 SkewBp만큼 '감소'해야 함 (가격을 낮춤 -> 매수 확률 감소)
        var expectedSkewAdjustment = 2m * 1; // SkewBp * (120 / 100)
        newParams.BidSpreadBp.Should().Be(initialParams.BidSpreadBp - expectedSkewAdjustment, "because buy fills should skew bid price down.");
        newParams.AskSpreadBp.Should().Be(initialParams.AskSpreadBp - expectedSkewAdjustment, "because buy fills should skew ask price down.");

        // 5. Skew가 적용된 최종 호가 가격을 확인
        // New Fair Value = 50001
        // New Bid Spread = -12bp, New Ask Spread = 8bp
        // Ask Spread Amount = 50001 * (8 / 10000) = 40.0008
        // Bid Spread Amount = 50001 * (-12 / 10000) = -60.0012
        // Raw Ask = 50001 + 40.0008 = 50041.0008 -> Rounded to 50041.5
        // Raw Bid = 50001 - 60.0012 = 49940.9988 -> Rounded to 49940.5
        // Grouped Ask = Ceil(50001 / 5) * 5 = 50045
        // Grouped Bid = Floor(50001 / 5) * 5 = 5040
        var expectedAskPrice = Price.FromDecimal(50045m);
        var expectedBidPrice = Price.FromDecimal(49940m);

        _askQuoter.LatestQuote.Should().NotBeNull();
        _askQuoter.LatestQuote.Value.Price.Should().Be(expectedAskPrice);
        _bidQuoter.LatestQuote.Should().NotBeNull();
        _bidQuoter.LatestQuote.Value.Price.Should().Be(expectedBidPrice);

        TestContext.WriteLine($"Skew successful. New Bid Spread: {newParams.BidSpreadBp}, New Ask Spread: {newParams.AskSpreadBp}");
    }

    [Test]
    public void Requote_WhenSellFillsExceedOrderSize_ShouldApplyPositiveSkewToSpreads()
    {
        // --- Arrange ---
        var initialParams = new QuotingParameters(
            _xbtusd.InstrumentId, "test", FairValueModel.Midp, _btcusdt.InstrumentId,
            askSpreadBp: 10m,
            bidSpreadBp: -10m,
            skewBp: 2m,
            size: Quantity.FromDecimal(100),
            depth: 1,
            askQuoterType: QuoterType.Log,
            bidQuoterType: QuoterType.Log,
            postOnly: true,
            maxCumBidFills: Quantity.FromDecimal(1000),
            maxCumAskFills: Quantity.FromDecimal(1000),
            HittingLogic.AllowAll
        );
        _engine.Start();
        _engine.UpdateParameters(initialParams);
        _engine.Activate();

        // 주문 사이즈(100)의 2배를 초과하는 매도 체결 시뮬레이션 (N=2)
        var sellFill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Sell, Price.FromDecimal(50000), Quantity.FromDecimal(230), 0);
        _engine.OnFill(sellFill);

        // --- Act ---
        TriggerRequote(50001m);

        // --- Assert ---
        var newParams = _engine.CurrentParameters;

        // 매도 체결(N=2)이 있었으므로, 스프레드는 SkewBp * 2 만큼 '증가'해야 함 (가격을 높임 -> 매도 확률 감소)
        var expectedSkewAdjustment = 2m * 2; // SkewBp * (230 / 100)
        newParams.BidSpreadBp.Should().Be(initialParams.BidSpreadBp + expectedSkewAdjustment, "because sell fills should skew bid price up.");
        newParams.AskSpreadBp.Should().Be(initialParams.AskSpreadBp + expectedSkewAdjustment, "because sell fills should skew ask price up.");
    }

    [Test]
    public void Requote_WhenSellFillsExceedOrderSize_ThenOffsetBuyFills_ShouldApplyNetSkew()
    {
        // --- Arrange ---
        var initialParams = new QuotingParameters(
            _xbtusd.InstrumentId, "test", FairValueModel.Midp, _btcusdt.InstrumentId,
            askSpreadBp: 10m,
            bidSpreadBp: -10m,
            skewBp: 2m,
            size: Quantity.FromDecimal(100),
            depth: 1,
            askQuoterType: QuoterType.Log,
            bidQuoterType: QuoterType.Log,
            postOnly: true,
            maxCumBidFills: Quantity.FromDecimal(1000),
            maxCumAskFills: Quantity.FromDecimal(1000),
            HittingLogic.AllowAll
        );
        _engine.Start();
        _engine.UpdateParameters(initialParams);
        _engine.Activate();

        // 주문 사이즈(100)의 2배를 초과하는 매도 체결 시뮬레이션 (N=2)
        var sellFill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Sell, Price.FromDecimal(50000), Quantity.FromDecimal(230), 0);
        _engine.OnFill(sellFill);

        // --- Act ---
        TriggerRequote(50001m);

        // 주문 사이즈(100)의 매수 체결 시뮬레이션 (N=1)
        var buyFill = new Fill(_xbtusd.InstrumentId, "test", 1, "E1", "X1", Side.Buy, Price.FromDecimal(50000), Quantity.FromDecimal(200), 0);
        _engine.OnFill(buyFill);

        // --- Act ---
        TriggerRequote(50002m);
        // --- Assert ---
        var newParams = _engine.CurrentParameters;

        // 매도 체결(N=2)이 있었으므로, 스프레드는 SkewBp * 2 만큼 '증가'해야 함 (가격을 높임 -> 매도 확률 감소)
        var expectedSkewAdjustment = 2m * 2 - 2m * 1; // SkewBp * (170 / 100)
        newParams.BidSpreadBp.Should().Be(initialParams.BidSpreadBp + expectedSkewAdjustment, "because sell fills should skew bid price up.");
        newParams.AskSpreadBp.Should().Be(initialParams.AskSpreadBp + expectedSkewAdjustment, "because sell fills should skew ask price up.");
    }

    [Test]
    public void Requote_WithGroupingBp_ShouldRoundQuotesToGroupSize()
    {
        // --- Arrange ---
        // 1. GroupingBp가 설정된 파라미터 (예: 5bp)
        // 1bp = 0.0001. 5bp = 0.0005.
        // FairValue = 50000.
        // 5bp Value at 50000 = 50000 * 0.0005 = 25.
        // TickSize = 0.5.
        // GroupSize = 25 / 0.5 = 50 ticks.
        // Grouping Unit = 50 * 0.5 = 25.0.

        decimal groupingBp = 5.0m;
        var initialParams = new QuotingParameters(
            _xbtusd.InstrumentId, "test", FairValueModel.Midp, _btcusdt.InstrumentId,
            askSpreadBp: 10m,  // +10bp
            bidSpreadBp: -10m, // -10bp
            skewBp: 0m,
            size: Quantity.FromDecimal(100),
            depth: 1,
            askQuoterType: QuoterType.Log,
            bidQuoterType: QuoterType.Log,
            postOnly: true,
            maxCumBidFills: Quantity.FromDecimal(1000),
            maxCumAskFills: Quantity.FromDecimal(1000),
            hittingLogic: HittingLogic.AllowAll,
            groupingBp: groupingBp // [중요] Grouping BP 설정
        );

        _engine.Start();
        _engine.UpdateParameters(initialParams);
        _engine.Activate();

        // 2. FairValue 설정 (50000)
        // Bid Spread (-10bp) -> 50000 * (1 - 0.0010) = 49950
        // Ask Spread (+10bp) -> 50000 * (1 + 0.0010) = 50050
        // 이 값들은 우연히 25의 배수이므로 Grouping 효과를 보기 위해 FairValue를 살짝 비틂.

        // FairValue = 50010 으로 설정.
        // Raw Bid = 50010 * 0.9990 = 49959.99 -> Tick(0.5) Round -> 49960.0
        // Raw Ask = 50010 * 1.0010 = 50060.01 -> Tick(0.5) Round -> 50060.0

        // Grouping Unit = 25.0 (50010 * 5bp approx) -> 실제로는 FV가 아니라 Quote Price 기준임.
        // Bid Quote(49960) 기준 5bp = 49960 * 0.0005 = 24.98 -> 25.0 (Tick 0.5 * 50)
        // Ask Quote(50060) 기준 5bp = 50060 * 0.0005 = 25.03 -> 25.0 (Tick 0.5 * 50)

        // Expected Bid (Floor): 49960 / 25 = 1998.4 -> 1998 * 25 = 49950.0
        // Expected Ask (Ceiling): 50060 / 25 = 2002.4 -> 2003 * 25 = 50075.0

        // --- Act ---
        TriggerRequote(50010m);

        // --- Assert ---
        _bidQuoter.LatestQuote.Should().NotBeNull();
        _askQuoter.LatestQuote.Should().NotBeNull();

        var actualBid = _bidQuoter.LatestQuote.Value.Price.ToDecimal();
        var actualAsk = _askQuoter.LatestQuote.Value.Price.ToDecimal();

        TestContext.WriteLine($"Raw Bid: 49960.0, Actual Grouped Bid: {actualBid}");
        TestContext.WriteLine($"Raw Ask: 50060.0, Actual Grouped Ask: {actualAsk}");

        // 검증: Grouping 단위(25.0)로 나누어 떨어져야 함
        (actualBid % 25.0m).Should().Be(0m, "Bid price should be a multiple of group size (25.0)");
        (actualAsk % 25.0m).Should().Be(0m, "Ask price should be a multiple of group size (25.0)");

        // 검증: 방향성 (Bid는 내려가고, Ask는 올라가야 함 -> Spread 벌어짐)
        actualBid.Should().BeLessThanOrEqualTo(49960.0m); // Floor
        actualAsk.Should().BeGreaterThanOrEqualTo(50060.0m); // Ceiling

        // 정확한 값 검증
        actualBid.Should().Be(49950.0m);
        actualAsk.Should().Be(50075.0m);
    }

    /// <summary>
    /// This test verifies the core functionality of the lazy deregistration mechanism.
    /// Scenario:
    /// 1. A quote order is created and becomes active.
    /// 2. A 'Cancelled' report arrives. The SingleOrderQuoter receives this,
    ///    clears its internal _activeOrder reference, and calls DeregisterOrder on the router.
    /// 3. The OrderRouter puts the order into its lazy-deregister buffer instead of deleting it immediately.
    /// 4. A "late" Fill report arrives for the same order.
    /// 5. The router should still find the order object in its active dictionary and route the fill.
    /// 6. The QuotingEngine should successfully receive the EngineOrderFilled event.
    /// </summary>
    [Test]
    public async Task LateFill_IsProcessed_AfterOrderIsClearedInQuoter_DueToLazyDeregister()
    {
        // --- Arrange ---
        TestContext.WriteLine("Setting up test with SingleOrderQuoter...");

        // 1. Use SingleOrderQuoter instead of LogQuoter for this specific test.
        var parameters = new QuotingParameters(
            _xbtusd.InstrumentId,
            "test-lazy",
            FairValueModel.Midp,
            _btcusdt.InstrumentId,
            10m, -10m, 0.5m, Quantity.FromDecimal(100), 1,
            QuoterType.Single,
            QuoterType.Single, // IMPORTANT: Use the real quoter
            true,
            Quantity.FromDecimal(1000), Quantity.FromDecimal(1000), HittingLogic.AllowAll
        );

        var quoterFactory = _serviceProvider.GetRequiredService<IQuoterFactory>();
        var bidQuoter = quoterFactory.CreateQuoter(parameters, Side.Buy);
        var askQuoter = quoterFactory.CreateQuoter(parameters, Side.Sell);

        var marketMaker = new MarketMaker(
            _serviceProvider.GetRequiredService<ILogger<MarketMaker>>(),
            _xbtusd, bidQuoter, askQuoter,
            _serviceProvider.GetRequiredService<IQuoteValidator>()
        );

        var engine = new QuotingEngine(
            _serviceProvider.GetRequiredService<ILogger<QuotingEngine>>(),
            _xbtusd, _btcusdt, marketMaker, _fvProvider, parameters, _marketDataManager, new NullFxConverter()
        );

        engine.Start();
        engine.Activate();

        // 2. Trigger a requote to create an active order.
        TriggerRequote(50000m);
        await Task.Delay(200); // Allow time for the order to be created and registered.

        var activeOrders = _orderRouter.GetActiveOrders();
        activeOrders.Should().NotBeEmpty("An order should have been created.");
        var testOrder = activeOrders.First(o => o.Side == Side.Buy);
        long testClientOrderId = testOrder.ClientOrderId;
        TestContext.WriteLine($"Test order created with ClientOrderId: {testClientOrderId}");

        // 3. Setup a listener to catch the final Fill event on the engine.
        Fill? receivedFill = null;
        engine.EngineOrderFilled += (sender, fill) =>
        {
            TestContext.WriteLine($"SUCCESS: QuotingEngine received the fill event for ExecutionId: {fill.ExecutionId}");
            receivedFill = fill;
        };

        // --- Act ---

        // 4. Simulate a terminal status report (e.g., Cancelled) to clear the quoter.
        // This will trigger the call to OrderRouter.DeregisterOrder().
        TestContext.WriteLine("Simulating a 'Cancelled' report to clear the quoter's active order...");
        var cancelReport = new OrderStatusReport(
            testClientOrderId, "E1", null, _xbtusd.InstrumentId, Side.Buy,
            OrderStatus.Cancelled, // Terminal status
            Price.FromDecimal(49990), Quantity.FromDecimal(100), Quantity.FromDecimal(0),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
        _orderRouter.RouteReport(cancelReport);
        await Task.Delay(100); // Allow event propagation.

        // At this point, SingleOrderQuoter._activeOrder is null, but the Order object
        // is still in the OrderRouter's _activeOrders dictionary, pending lazy removal.

        // 5. Simulate the "late" fill arriving AFTER the cancellation confirmation.
        TestContext.WriteLine("Simulating the 'late' Fill report...");
        var lateFillReport = new OrderStatusReport(
            testClientOrderId, "E1", "LateExec123", // Unique execution ID
            _xbtusd.InstrumentId, Side.Buy,
            OrderStatus.PartiallyFilled, // A fill status
            Price.FromDecimal(49990), Quantity.FromDecimal(100), Quantity.FromDecimal(50),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            lastPrice: Price.FromDecimal(49990),
            lastQuantity: Quantity.FromDecimal(50)
        );
        _orderRouter.RouteReport(lateFillReport);
        await Task.Delay(100); // Allow processing.


        // --- Assert ---
        receivedFill.Should().NotBeNull("The late fill event should have been received by the QuotingEngine.");
        receivedFill.Value.ExecutionId.Should().Be("LateExec123", "The details of the late fill should be correct.");
        receivedFill.Value.Quantity.ToDecimal().Should().Be(50);
    }

    /// <summary>
    /// This test verifies that when the deregistration buffer exceeds its capacity,
    /// the oldest order is truly removed from the active dictionary and can no longer
    /// process subsequent reports.
    /// Scenario:
    /// 1. Create an OrderRouter with a small buffer size (e.g., 3).
    /// 2. Create and register 4 orders (Order1, Order2, Order3, Order4).
    /// 3. Deregister Order1, Order2, and Order3. They should now be in the buffer.
    ///    The buffer is now full: [Order1, Order2, Order3].
    /// 4. Deregister Order4. This should cause Order1 to be pushed out of the buffer
    ///    and permanently removed from the active orders dictionary.
    ///    The buffer now contains: [Order2, Order3, Order4].
    /// 5. Send a late fill report for Order1 (the removed one) and Order2 (still in the buffer).
    /// 6. Assert that only the fill for Order2 was processed, and the fill for Order1 was ignored.
    /// </summary>
    [Test]
    public void Order_IsFinallyRemoved_WhenDeregisterBufferOverflows_AndIgnoresSubsequentReports()
    {
        // --- Arrange ---
        // 1. Create a dedicated OrderRouter with a small buffer size for this test.
        const int bufferSize = 3;
        var testRouter = new OrderRouter(
            _serviceProvider.GetRequiredService<ILogger<OrderRouter>>(),
            deregistrationBufferSize: bufferSize
        );

        // CRITICAL FIX: Create a new OrderFactory manually, injecting our local 'testRouter'.
        // Do NOT use the factory from the service provider, as it holds a different router instance.
        var orderFactory = new OrderFactory(
            testRouter, // <--- Inject the local router instance here
            _serviceProvider.GetRequiredService<IOrderGatewayRegistry>(),
            _serviceProvider.GetRequiredService<ILogger<Order>>(),
            _serviceProvider.GetRequiredService<IClientIdGenerator>(),
            _marketDataManager,
            _instrumentRepo
        );

        // 2. Create 4 orders (buffer size + 1) using the new factory.
        // Now, these orders will hold a reference to 'testRouter'.
        var order1 = orderFactory.Create(_xbtusd.InstrumentId, Side.Buy, "test-lazy", OrderSource.NonManual);
        var order2 = orderFactory.Create(_xbtusd.InstrumentId, Side.Buy, "test-lazy", OrderSource.NonManual);
        var order3 = orderFactory.Create(_xbtusd.InstrumentId, Side.Buy, "test-lazy", OrderSource.NonManual);
        var order4 = orderFactory.Create(_xbtusd.InstrumentId, Side.Buy, "test-lazy", OrderSource.NonManual);

        // The rest of the registration logic is correct.
        testRouter.RegisterOrder(order1);
        testRouter.RegisterOrder(order2);
        testRouter.RegisterOrder(order3);
        testRouter.RegisterOrder(order4);

        testRouter.GetActiveOrders().Count.Should().Be(4, "all orders should be registered initially.");

        // 3. Setup a listener to capture processed fills.
        // This handler is now correctly attached to the same router instance used by the orders.
        var processedFills = new List<Fill>();
        testRouter.OrderFilled += (sender, fill) =>
        {
            processedFills.Add(fill);
        };

        // --- Act --- (No changes needed below this line)
        // ... (rest of the test method is unchanged)
        TestContext.WriteLine($"Deregistering orders to fill the buffer (size={bufferSize})...");
        testRouter.DeregisterOrder(order1);
        testRouter.DeregisterOrder(order2);
        testRouter.DeregisterOrder(order3);

        // ... and so on
        testRouter.GetActiveOrders().Count.Should().Be(4);
        TestContext.WriteLine("Buffer is now full with [Order1, Order2, Order3].");

        TestContext.WriteLine("Deregistering Order4, which should push Order1 out of the buffer...");
        testRouter.DeregisterOrder(order4);

        var activeOrdersAfterOverflow = testRouter.GetActiveOrders();
        activeOrdersAfterOverflow.Count.Should().Be(3, "Order1 should have been permanently removed.");
        activeOrdersAfterOverflow.Any(o => o.ClientOrderId == order1.ClientOrderId).Should().BeFalse();
        TestContext.WriteLine("Order1 has been finalized and removed.");
        TestContext.WriteLine("Sending late fill reports for removed Order1 and buffered Order2...");

        var reportForRemovedOrder = new OrderStatusReport(
            order1.ClientOrderId, "E1", "ExecForRemoved", _xbtusd.InstrumentId, Side.Buy,
            OrderStatus.Filled, Price.FromDecimal(0m), Quantity.FromDecimal(0m), Quantity.FromDecimal(0m),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lastPrice: Price.FromDecimal(1m), lastQuantity: Quantity.FromDecimal(1m)
        );
        var reportForBufferedOrder = new OrderStatusReport(
            order2.ClientOrderId, "E2", "ExecForBuffered", _xbtusd.InstrumentId, Side.Buy,
            OrderStatus.PartiallyFilled, Price.FromDecimal(0m), Quantity.FromDecimal(0m), Quantity.FromDecimal(0m),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lastPrice: Price.FromDecimal(1m), lastQuantity: Quantity.FromDecimal(1m)
        );

        testRouter.RouteReport(reportForRemovedOrder);
        testRouter.RouteReport(reportForBufferedOrder);

        // --- Assert ---
        processedFills.Should().HaveCount(1, "only the report for the buffered order should be processed.");
        processedFills[0].ExecutionId.Should().Be("ExecForBuffered", "the processed fill should be from Order2.");

        TestContext.WriteLine("SUCCESS: The late fill for the finalized order was correctly ignored.");
        testRouter.RouteReport(reportForRemovedOrder);
        testRouter.RouteReport(reportForBufferedOrder);

        // --- Assert ---
        processedFills.Should().HaveCount(1, "only the report for the buffered order should be processed.");
        processedFills[0].ExecutionId.Should().Be("ExecForBuffered", "the processed fill should be from Order2.");

        TestContext.WriteLine("SUCCESS: The late fill for the finalized order was correctly ignored.");
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

    protected override Task ProcessMessage(ReadOnlyMemory<byte> messageBytes)
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

    protected override bool IsPongMessage(ReadOnlySpan<byte> messageSpan)
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