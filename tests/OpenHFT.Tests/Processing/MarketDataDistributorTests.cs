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
using Microsoft.Extensions.DependencyInjection;
using OpenHFT.Core.Orders;
using Castle.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace OpenHFT.Tests.Processing;

[TestFixture]
public class MarketDataDistributorTests
{
    private ServiceProvider _serviceProvider = null!;
    private string _testDirectory;
    private MockAdapter _mockAdapter = null!;
    private TestConsumer _btcConsumer = null!;
    private TestConsumer _ethConsumer = null!;
    private readonly ExchangeTopic _testTopic = BinanceTopic.AggTrade;
    private Instrument _btc;
    private Instrument _eth;
    [SetUp]
    public void Setup()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "InstrumentRepositoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,Spot,BTC,USDT,0.01,0.00001,1,10
BINANCE,ETHUSDT,Spot,ETH,USDT,0.01,0.0001,1,10
BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.1,0.001,1,0.001
BINANCE,ETHUSDT,PerpetualFuture,ETH,USDT,0.01,0.0001,1,0.001";
        File.WriteAllText(Path.Combine(_testDirectory, "instruments.csv"), csvContent);
        var inMemorySettings = new Dictionary<string, string>
        {
            { "dataFolder", _testDirectory }
        };
        Microsoft.Extensions.Configuration.IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton<IInstrumentRepository, InstrumentRepository>();
        var instrumentRepository = new InstrumentRepository(new NullLogger<InstrumentRepository>(), configuration);
        instrumentRepository.StartAsync().Wait();

        // --- 2. 테스트용 Consumer(옵저버) 생성 ---
        _btc = instrumentRepository.FindBySymbol("BTCUSDT", ProductType.Spot, ExchangeEnum.BINANCE);
        _eth = instrumentRepository.FindBySymbol("ETHUSDT", ProductType.Spot, ExchangeEnum.BINANCE);
        if (_btc == null || _eth == null)
            throw new Exception("Instruments not found in repository.");
        _btcConsumer = new TestConsumer(new NullLogger<TestConsumer>(), _btc, "BTC_Consumer", _testTopic);
        _ethConsumer = new TestConsumer(new NullLogger<TestConsumer>(), _eth, "ETH_Consumer", _testTopic);

        _mockAdapter = new MockAdapter(new NullLogger<MockAdapter>(), ProductType.PerpetualFuture, instrumentRepository);
        services.AddSingleton<IFeedAdapter>(_mockAdapter);
        services.AddSingleton<MarketDataDistributor>();
        services.AddSingleton<IOrderRouter, OrderRouter>();
        services.AddSingleton<IOrderUpdateHandler, OrderUpdateDistributor>();

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
        services.AddSingleton<IFeedAdapterRegistry, FeedAdapterRegistry>();
        services.AddSingleton<IFeedHandler, FeedHandler>();
        _serviceProvider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        // 테스트가 끝난 후 Disruptor를 안전하게 종료
        _serviceProvider?.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Test]
    public async Task Distributor_ShouldDistributeEvents_ToCorrectlySubscribedConsumers()
    {
        var disruptor = _serviceProvider.GetRequiredService<Disruptor<MarketDataEventWrapper>>();
        var distributor = _serviceProvider.GetRequiredService<MarketDataDistributor>();

        _serviceProvider.GetRequiredService<IFeedHandler>();

        distributor.Subscribe(_btcConsumer);
        distributor.Subscribe(_ethConsumer);

        var ringBufer = disruptor.Start();
        var repository = _serviceProvider.GetRequiredService<IInstrumentRepository>();
        // 테스트용 이벤트 생성
        var btcUpdates = new PriceLevelEntryArray();
        var ethUpdates = new PriceLevelEntryArray();
        btcUpdates[0] = new PriceLevelEntry(Side.Buy, 50000m, 100m);
        ethUpdates[0] = new PriceLevelEntry(Side.Sell, 3000m, 200m);

        var btcEvent = new MarketDataEvent(1, 1, EventKind.Update, _btc.InstrumentId, _btc.SourceExchange, 0, _testTopic.TopicId, 1, btcUpdates);
        var ethEvent = new MarketDataEvent(2, 2, EventKind.Update, _eth.InstrumentId, _eth.SourceExchange, 0, _testTopic.TopicId, 1, ethUpdates);

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
        _btcConsumer.ReceivedEvents[0].InstrumentId.Should().Be(_btc.InstrumentId);

        // 2. ETH Consumer는 ETH 이벤트만 받아야 함
        _ethConsumer.ReceivedEvents.Should().HaveCount(1);
        _ethConsumer.ReceivedEvents[0].InstrumentId.Should().Be(_eth.InstrumentId);
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
}