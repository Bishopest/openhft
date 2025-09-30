using NUnit.Framework;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Disruptor;
using Disruptor.Dsl;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Processing;
using OpenHFT.Processing.Interfaces;
using System.Collections.Concurrent;

namespace OpenHFT.Tests.Processing;

[TestFixture]
public class MarketDataDistributorTests
{
    // 테스트에 사용될 핵심 컴포넌트들
    private Disruptor<MarketDataEventWrapper> _disruptor = null!;
    private Mock<IFeedAdapter> _mockAdapter = null!;
    private FeedHandler _feedHandler = null!;
    private MarketDataDistributor _distributor = null!;

    // 테스트 결과를 확인할 테스트용 Consumer들
    private TestConsumer _btcConsumer = null!;
    private TestConsumer _ethConsumer = null!;
    private TestConsumer _allTopicsConsumer = null!;

    private ILogger<MarketDataDistributor> _logger = null!;

    [SetUp]
    public void Setup()
    {
        // --- 1. 로깅 팩토리 생성 (모든 컴포넌트가 공유) ---
        var loggerFactory = LoggerFactory.Create(builder => { });
        _logger = loggerFactory.CreateLogger<MarketDataDistributor>();

        // --- 2. 테스트용 Consumer(옵저버) 생성 ---
        var btc = SymbolUtils.GetSymbolId("BTCUSDT");
        var eth = SymbolUtils.GetSymbolId("ETHUSDT");
        _btcConsumer = new TestConsumer(btc, ExchangeEnum.BINANCE, "BTC_Consumer", new[] { "BTCUSDT@depth" });
        _ethConsumer = new TestConsumer(eth, ExchangeEnum.BINANCE, "ETH_Consumer", new[] { "ETHUSDT@depth" });
        var allConsumers = new List<IMarketDataConsumer> { _btcConsumer, _ethConsumer };

        // --- 3. Disruptor 인스턴스 생성 ---
        _disruptor = new Disruptor<MarketDataEventWrapper>(
            () => new MarketDataEventWrapper(),
            1024,
            TaskScheduler.Default,
            ProducerType.Multi,
            new BlockingWaitStrategy()
        );

        // --- 4. Mock IFeedAdapter 생성 ---
        _mockAdapter = new Mock<IFeedAdapter>();
        _mockAdapter.Setup(a => a.SourceExchange).Returns(ExchangeEnum.BINANCE);

        // --- 5. 핵심 컴포넌트들을 '수동'으로 조립 ---
        // a) Distributor 생성 (Disruptor와 로거 주입)
        _distributor = new MarketDataDistributor(
            _disruptor,
            loggerFactory.CreateLogger<MarketDataDistributor>()
        );

        // b) FeedHandler 생성 (어댑터와 Disruptor 주입)
        var adapters = new ConcurrentDictionary<ExchangeEnum, IFeedAdapter>();
        adapters.TryAdd(ExchangeEnum.BINANCE, _mockAdapter.Object);
        _feedHandler = new FeedHandler(
            loggerFactory.CreateLogger<FeedHandler>(),
            adapters,
            _disruptor
        );

        // c) Distributor가 모든 Consumer를 구독하도록 설정
        foreach (var consumer in allConsumers)
        {
            _distributor.Subscribe(consumer);
        }
    }

    [TearDown]
    public void TearDown()
    {
        // 테스트가 끝난 후 Disruptor를 안전하게 종료
        _disruptor?.Shutdown(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Distributor_ShouldDistributeEvents_ToCorrectlySubscribedConsumers()
    {
        // --- Arrange ---
        // Distributor를 이벤트 핸들러로 등록하고 Disruptor 시작
        _disruptor.HandleEventsWith(_distributor);
        var ringBuffer = _disruptor.Start();

        var btcSymbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var ethSymbolId = SymbolUtils.GetSymbolId("ETHUSDT");
        var exchange = ExchangeEnum.BINANCE;
        // 테스트용 이벤트 생성
        var btcEvent = new MarketDataEvent(1, 1, Side.Buy, 50000, 100, EventKind.Update, btcSymbolId, exchange);
        var ethEvent = new MarketDataEvent(2, 2, Side.Sell, 3000, 200, EventKind.Update, ethSymbolId, exchange);

        // --- Act ---
        // Mock 어댑터에서 이벤트를 '발생'시킴.
        // 이는 _feedHandler.OnMarketDataReceived를 트리거하고, 데이터는 Disruptor에 기록됨.
        _mockAdapter.Raise(m => m.MarketDataReceived += null, this, btcEvent);
        _mockAdapter.Raise(m => m.MarketDataReceived += null, this, ethEvent);

        // Distributor의 소비자 스레드가 이벤트를 처리할 충분한 시간을 줌
        await Task.Delay(200);

        // --- Assert ---
        // 1. BTC Consumer는 BTC 이벤트만 받아야 함
        _btcConsumer.ReceivedEvents.Should().HaveCount(1);
        _btcConsumer.ReceivedEvents[0].InstrumentId.Should().Be(btcSymbolId);

        // 2. ETH Consumer는 ETH 이벤트만 받아야 함
        _ethConsumer.ReceivedEvents.Should().HaveCount(1);
        _ethConsumer.ReceivedEvents[0].InstrumentId.Should().Be(ethSymbolId);
    }
}

// --- 테스트를 위한 Helper 클래스 (수정) ---
public class TestConsumer : IMarketDataConsumer
{
    public string ConsumerName { get; }
    public int SymbolId { get; }
    public ExchangeEnum Exchange { get; }
    private readonly IEnumerable<string> _subscriptionKeys;
    public List<MarketDataEvent> ReceivedEvents { get; } = new();

    public int Priority => 1;


    public TestConsumer(int symbolId, ExchangeEnum exchange, string consumerName, IEnumerable<string> subscriptionKeys)
    {
        ConsumerName = consumerName;
        Exchange = exchange;
        SymbolId = symbolId;
        _subscriptionKeys = subscriptionKeys;
    }

    public IEnumerable<string> GetSubscriptions()
    {
        return _subscriptionKeys;
    }

    public Task OnMarketData(MarketDataEvent marketEvent)
    {
        // 동시성 문제를 피하기 위해 리스트를 잠금
        lock (ReceivedEvents)
        {
            ReceivedEvents.Add(marketEvent);
        }

        return Task.CompletedTask;
    }
}