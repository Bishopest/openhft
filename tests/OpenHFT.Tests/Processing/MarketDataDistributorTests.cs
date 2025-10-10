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
using OpenHFT.Core.Instruments;
using OpenHFT.Feed.Adapters;
using System.Net.WebSockets;
using OpenHFT.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using OpenHFT.Feed.Models;

namespace OpenHFT.Tests.Processing;

[TestFixture]
public class MarketDataDistributorTests
{
    // 테스트에 사용될 핵심 컴포넌트들
    private Disruptor<MarketDataEventWrapper> _disruptor = null!;
    private MockAdapter _mockAdapter = null!;
    private FeedHandler _feedHandler = null!;
    private MarketDataDistributor _distributor = null!;

    // 테스트 결과를 확인할 테스트용 Consumer들
    private TestConsumer _btcConsumer = null!;
    private TestConsumer _ethConsumer = null!;
    private ExchangeTopic _testTopic = null;

    private ILogger<MarketDataDistributor> _logger = null!;
    private string _testDirectory;
    private InstrumentRepository _repository;

    [SetUp]
    public void Setup()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "InstrumentRepositoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        _repository = new InstrumentRepository(new NullLogger<InstrumentRepository>());
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,Spot,BTC,USDT,0.01,0.00001,1,10
BINANCE,ETHUSDT,Spot,ETH,USDT,0.01,0.0001,1,10
BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.1,0.001,1,0.001
BINANCE,ETHUSDT,PerpetualFuture,ETH,USDT,0.01,0.0001,1,0.001";

        var filePath = CreateTestCsvFile(csvContent);
        _repository.LoadFromCsv(filePath);

        // --- 1. 로깅 팩토리 생성 (모든 컴포넌트가 공유) ---
        var loggerFactory = LoggerFactory.Create(builder => { });
        _logger = loggerFactory.CreateLogger<MarketDataDistributor>();

        // --- 2. 테스트용 Consumer(옵저버) 생성 ---
        var btc = _repository.FindBySymbol("BTCUSDT", ProductType.Spot, ExchangeEnum.BINANCE);
        var eth = _repository.FindBySymbol("ETHUSDT", ProductType.Spot, ExchangeEnum.BINANCE);
        _testTopic = BinanceTopic.AggTrade;
        _btcConsumer = new TestConsumer(_logger, btc, "BTC_Consumer", _testTopic);
        _ethConsumer = new TestConsumer(_logger, eth, "ETH_Consumer", _testTopic);
        var allConsumers = new List<BaseMarketDataConsumer> { _btcConsumer, _ethConsumer };

        // --- 3. Disruptor 인스턴스 생성 ---
        _disruptor = new Disruptor<MarketDataEventWrapper>(
            () => new MarketDataEventWrapper(),
            1024,
            TaskScheduler.Default,
            ProducerType.Multi,
            new BlockingWaitStrategy()
        );

        // --- 4. Mock IFeedAdapter 생성 ---
        _mockAdapter = new MockAdapter(_logger, ProductType.PerpetualFuture, null);

        // --- 5. 핵심 컴포넌트들을 '수동'으로 조립 ---
        // a) Distributor 생성 (Disruptor와 로거 주입)
        _distributor = new MarketDataDistributor(
            _disruptor,
            loggerFactory.CreateLogger<MarketDataDistributor>()
        );

        // b) FeedHandler 생성 (어댑터와 Disruptor 주입)
        var adapters = new ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, BaseFeedAdapter>>();
        var innerDict = adapters.GetOrAdd(ExchangeEnum.BINANCE, _ => new ConcurrentDictionary<ProductType, BaseFeedAdapter>());
        innerDict.TryAdd(ProductType.PerpetualFuture, _mockAdapter);
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
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    private string CreateTestCsvFile(string content)
    {
        var filePath = Path.Combine(_testDirectory, "instruments.csv");
        File.WriteAllText(filePath, content);
        return filePath;
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
        var btcEvent = new MarketDataEvent(1, 1, Side.Buy, 50000, 100, EventKind.Update, btcSymbolId, exchange, 0, _testTopic.TopicId);
        var ethEvent = new MarketDataEvent(2, 2, Side.Sell, 3000, 200, EventKind.Update, ethSymbolId, exchange, 0, _testTopic.TopicId);

        // --- Act ---
        // Mock 어댑터에서 이벤트를 '발생'시킴.
        // 이는 _feedHandler.OnMarketDataReceived를 트리거하고, 데이터는 Disruptor에 기록됨.
        _mockAdapter.FireMarketDataEvent(btcEvent);
        _mockAdapter.FireMarketDataEvent(ethEvent);

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
public class TestConsumer : BaseMarketDataConsumer
{
    public override string ConsumerName { get; }
    public override IReadOnlyCollection<Instrument> Instruments { get; }
    private readonly IEnumerable<string> _subscriptionKeys;
    public List<MarketDataEvent> ReceivedEvents { get; } = new();

    private ExchangeTopic? _topic = null;
    public TestConsumer(ILogger logger, Instrument instrument, string consumerName, ExchangeTopic topic) : base(logger, topic)
    {
        ConsumerName = consumerName;
        Instruments = new List<Instrument> { instrument };
        _topic = topic;
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

    protected override void OnMarketData(in MarketDataEvent data)
    {
        ReceivedEvents.Add(data);
    }
}

public class MockAdapter : BaseFeedAdapter
{
    public MockAdapter(ILogger logger, ProductType type, IInstrumentRepository instrumentRepository) : base(logger, type, instrumentRepository)
    {
    }

    public override ExchangeEnum SourceExchange => ExchangeEnum.BINANCE;

    protected override void ConfigureWebsocket(ClientWebSocket websocket)
    {
        throw new NotImplementedException();
    }

    protected override Task DoSubscribeAsync(IEnumerable<Instrument> insts, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    protected override Task DoUnsubscribeAsync(IEnumerable<Instrument> insts, CancellationToken cancellationToken)
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
}