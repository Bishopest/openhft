using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Disruptor;
using Disruptor.Dsl;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Core.Collections;
using OpenHFT.Core.OrderBook;
using System.Threading.Channels;
using OpenHFT.Core.Instruments;

namespace OpenHFT.Benchmarks;

public class MarketDataEventSlot
{
    public MarketDataEvent Value;
}

public class MarketDataEventClass
{
    public long Sequence { get; set; }
    public long Timestamp { get; set; }
    public Side Side { get; set; }
    public long PriceTicks { get; set; }
    public long Quantity { get; set; }
    public EventKind Kind { get; set; }
    public int SymbolId { get; set; }

    public MarketDataEventClass() { }
    public MarketDataEventClass(long sequence, long timestamp, Side side, long priceTicks,
                          long quantity, EventKind kind, int symbolId)
    {
        Sequence = sequence;
        Timestamp = timestamp;
        Side = side;
        PriceTicks = priceTicks;
        Quantity = quantity;
        Kind = kind;
        SymbolId = symbolId;
    }

    public override string ToString() =>
        $"MD[{Sequence}] {Kind} {Side} {PriceTicks}@{Quantity} @{Timestamp}μs";
}

public class SimpleSlotEventHandler : IEventHandler<MarketDataEventSlot>
{
    private long _eventCount;
    private readonly long _totalEventsToProcess;
    private readonly ManualResetEventSlim _signal;

    public void Reset()
    {
        Interlocked.Exchange(ref _eventCount, 0);
    }

    public SimpleSlotEventHandler(long totalEventsToProcess, ManualResetEventSlim signal)
    {
        _totalEventsToProcess = totalEventsToProcess;
        _signal = signal;
    }

    public void OnEvent(MarketDataEventSlot data, long sequence, bool endOfBatch)
    {
        ref readonly var actualEvent = ref data.Value;
        var count = Interlocked.Increment(ref _eventCount);

        if (count == _totalEventsToProcess)
        {
            _signal.Set();
        }
    }
}

public class SimpleEventHandler : IEventHandler<MarketDataEventClass>
{
    private long _eventCount;
    private readonly long _totalEventsToProcess;
    private readonly ManualResetEventSlim _signal;


    public SimpleEventHandler(long totalEventsToProcess, ManualResetEventSlim signal)
    {
        _totalEventsToProcess = totalEventsToProcess;
        _signal = signal;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _eventCount, 0);
    }

    public void OnEvent(MarketDataEventClass data, long sequence, bool endOfBatch)
    {
        var count = Interlocked.Increment(ref _eventCount);

        if (count == _totalEventsToProcess)
        {
            _signal.Set();
        }
    }
}

[MemoryDiagnoser]
[SimpleJob]
public class OrderBookBenchmarks
{
    private OrderBook _orderBook = null!;
    private MarketDataEvent[] _events = null!;
    private int _symbolId;

    [GlobalSetup]
    public void Setup()
    {
        _orderBook = new OrderBook(new CryptoPerpetual(
                instrumentId: 1001,
                symbol: "BTCUSDT",
                exchange: ExchangeEnum.BINANCE,
                baseCurrency: Currency.BTC,
                quoteCurrency: Currency.USDT,
                tickSize: Price.FromDecimal(0.1m),
                lotSize: Quantity.FromDecimal(0.001m),
                multiplier: 1m,
                minOrderSize: Quantity.FromDecimal(0.001m)
        ));
        _symbolId = SymbolUtils.GetSymbolId("BTCUSDT");

        // Pre-generate events to avoid allocation during benchmark
        var random = new Random(42); // Deterministic seed
        _events = new MarketDataEvent[10000];

        for (int i = 0; i < _events.Length; i++)
        {
            var side = random.Next(2) == 0 ? Side.Buy : Side.Sell;
            var basePrice = side == Side.Buy ? 49000 : 51000;
            var price = basePrice + random.Next(-100, 101);
            var quantity = random.Next(1, 1000) * 100000; // Random quantity in satoshis
            var entryArr = new PriceLevelEntryArray();
            entryArr[0] = new PriceLevelEntry(side, (decimal)price, (decimal)quantity);
            _events[i] = new MarketDataEvent(
                sequence: i + 1,
                timestamp: TimestampUtils.GetTimestampMicros(),
                kind: EventKind.Update,
                instrumentId: _symbolId,
                exchange: ExchangeEnum.BINANCE,
                updateCount: 1,
                updates: entryArr
            );
        }
    }

    [Benchmark]
    public void ApplySingleEvent()
    {
        var entryArr = new PriceLevelEntryArray();
        entryArr[0] = new PriceLevelEntry(Side.Buy, 50000m, 100000000m);
        var evt = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            kind: EventKind.Update,
            instrumentId: _symbolId,
            exchange: ExchangeEnum.BINANCE,
            updateCount: 1,
            updates: entryArr
        );

        _orderBook.ApplyEvent(evt);
    }

    [Benchmark]
    [Arguments(100)]
    [Arguments(1000)]
    [Arguments(10000)]
    public void ApplyEventBatch(int count)
    {
        for (int i = 0; i < count && i < _events.Length; i++)
        {
            _orderBook.ApplyEvent(_events[i]);
        }
    }

    [Benchmark]
    public void GetBestBidAsk()
    {
        var (bidPrice, bidQty) = _orderBook.GetBestBid();
        var (askPrice, askQty) = _orderBook.GetBestAsk();
    }

    [Benchmark]
    public void GetSpreadAndMid()
    {
        var spread = _orderBook.GetSpread();
        var mid = _orderBook.GetMidPrice();
    }

    [Benchmark]
    public void GetTopLevels()
    {
        var bidLevels = _orderBook.GetTopLevels(Side.Buy, 5);
        var askLevels = _orderBook.GetTopLevels(Side.Sell, 5);
    }

    [Benchmark]
    public void CalculateOrderFlowImbalance()
    {
        var ofi = _orderBook.CalculateOrderFlowImbalance(5);
    }
}

[MemoryDiagnoser]
[SimpleJob]
public class RingBufferBenchmarks
{
    private LockFreeRingBuffer<MarketDataEvent> _ringBuffer = null!;
    private MarketDataEvent[] _events = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ringBuffer = new LockFreeRingBuffer<MarketDataEvent>(65536);

        // Pre-generate events
        _events = new MarketDataEvent[10000];
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");

        for (int i = 0; i < _events.Length; i++)
        {
            var entryArr = new PriceLevelEntryArray();
            entryArr[0] = new PriceLevelEntry(Side.Buy, 50000m, 100000000m);
            _events[i] = new MarketDataEvent(
                sequence: i + 1,
                timestamp: TimestampUtils.GetTimestampMicros(),
                kind: EventKind.Update,
                instrumentId: symbolId,
                exchange: ExchangeEnum.BINANCE,
                updateCount: 1,
                updates: entryArr
            );
        }
    }

    [Benchmark]
    public bool WriteEvent()
    {
        return _ringBuffer.TryWrite(_events[0]);
    }

    [Benchmark]
    public bool ReadEvent()
    {
        return _ringBuffer.TryRead(out var evt);
    }

    [Benchmark]
    [Arguments(100)]
    [Arguments(1000)]
    [Arguments(10000)]
    public void WriteReadBatch(int count)
    {
        // Write batch
        for (int i = 0; i < count && i < _events.Length; i++)
        {
            _ringBuffer.TryWrite(_events[i]);
        }

        // Read batch
        for (int i = 0; i < count; i++)
        {
            _ringBuffer.TryRead(out var evt);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ringBuffer.Dispose();
    }
}

[MemoryDiagnoser]
[SimpleJob]
public class MPSC_RingBufferBenchmarks
{
    private MPSCRingBuffer<MarketDataEventClass> _mpscBuffer = null!;
    private MarketDataEventClass[] _events = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mpscBuffer = new MPSCRingBuffer<MarketDataEventClass>(65536);

        // Pre-generate events
        _events = new MarketDataEventClass[1];
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");

        _events[0] = new MarketDataEventClass(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            side: Side.Buy,
            priceTicks: PriceUtils.ToTicks(50000m),
            quantity: 100000000,
            kind: EventKind.Update,
            symbolId: symbolId
        );
    }

    // --- Single threads ---
    [Benchmark(Description = "MPSC: Single-threaded Write")]
    public bool SingleThread_WriteEvent()
    {
        return _mpscBuffer.TryWrite(_events[0]);
    }

    [Benchmark(Description = "MPSC: Single-threaded Read")]
    public bool SingleThread_ReadEvent()
    {
        return _mpscBuffer.TryRead(out var evt);
    }

    // --- Multi threads ---
    [Params(2, 4, 8)]
    public int ProducerCount { get; set; }

    [Benchmark(Description = "MPSC: Multi-threaded Write, Single-threaded Read")]
    public void MultiThread_WriteRead()
    {
        const int operationsPerProducer = 100_000;
        var totalOperations = operationsPerProducer * ProducerCount;

        var producers = new Task[ProducerCount];
        for (int i = 0; i < ProducerCount; i++)
        {
            producers[i] = Task.Run(() =>
            {
                for (int j = 0; j < operationsPerProducer; j++)
                {
                    // wait until full
                    while (!_mpscBuffer.TryWrite(_events[0]))
                    {
                        Thread.SpinWait(1);
                    }
                }
            });
        }

        int readCount = 0;
        while (readCount < totalOperations)
        {
            if (_mpscBuffer.TryRead(out var evt))
            {
                readCount++;
            }
            else
            {
                Thread.SpinWait(1);
            }
        }

        Task.WaitAll(producers);
    }

    [Benchmark(Description = "MPSC Lock: Bounded Multi-threaded Write")]
    public void Bounded_MultiThread_Write()
    {
        int operationsPerProducer = 65536 / ProducerCount - 1;

        var producers = new Task[ProducerCount];
        for (int i = 0; i < ProducerCount; i++)
        {
            producers[i] = Task.Run(() =>
            {
                for (int j = 0; j < operationsPerProducer; j++)
                {
                    _mpscBuffer.TryWrite(_events[0]);
                }
            });
        }

        Task.WaitAll(producers);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        while (_mpscBuffer.TryRead(out _)) { }
    }
}

[MemoryDiagnoser]
[SimpleJob]
public class MPSC_ChannelBenchmarks
{
    private Channel<MarketDataEventClass> _channel = null!;
    private ChannelWriter<MarketDataEventClass> _writer = null!;
    private ChannelReader<MarketDataEventClass> _reader = null!;
    private MarketDataEventClass[] _events = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new BoundedChannelOptions(65536)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        };
        _channel = Channel.CreateBounded<MarketDataEventClass>(options);
        _writer = _channel.Writer;
        _reader = _channel.Reader;

        // 단일 이벤트 객체 재사용 (메모리 할당 노이즈 제거)
        _events = new MarketDataEventClass[1];
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        _events[0] = new MarketDataEventClass(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            side: Side.Buy,
            priceTicks: PriceUtils.ToTicks(50000m),
            quantity: 100000000,
            kind: EventKind.Update,
            symbolId: symbolId
        );
    }

    // --- Single threads ---
    [Benchmark(Description = "Channel MPSC: Single-threaded Write")]
    public bool SingleThread_WriteEvent()
    {
        return _writer.TryWrite(_events[0]);
    }

    [Benchmark(Description = "Channel MPSC: Single-threaded Read")]
    public bool SingleThread_ReadEvent()
    {
        _writer.TryWrite(_events[0]);
        return _reader.TryRead(out var evt);
    }

    // --- Multi threads ---
    [Params(2, 4, 8)]
    public int ProducerCount { get; set; }

    [Benchmark(Description = "Channel MPSC: Multi-threaded Write, Single-threaded Read")]
    public async Task MultiThread_WriteRead()
    {
        const int operationsPerProducer = 100_000;
        var totalOperations = operationsPerProducer * ProducerCount;

        var producers = new Task[ProducerCount];
        for (int i = 0; i < ProducerCount; i++)
        {
            producers[i] = Task.Run(async () =>
            {
                for (int j = 0; j < operationsPerProducer; j++)
                {
                    await _writer.WriteAsync(_events[0]);
                }
            });
        }

        int readCount = 0;
        await foreach (var item in _reader.ReadAllAsync())
        {
            readCount++;
            if (readCount >= totalOperations)
            {
                break;
            }
        }

        await Task.WhenAll(producers);
    }

    [Benchmark(Description = "Channel MPSC: Bounded Multi-threaded Write")]
    public void Bounded_MultiThread_Write()
    {
        int operationsPerProducer = 65536 / ProducerCount - 1;

        var producers = new Task[ProducerCount];
        for (int i = 0; i < ProducerCount; i++)
        {
            producers[i] = Task.Run(() =>
            {
                for (int j = 0; j < operationsPerProducer; j++)
                {
                    _writer.TryWrite(_events[0]);
                }
            });
        }

        Task.WaitAll(producers);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        while (_reader.TryRead(out _)) { }
    }
}

[MemoryDiagnoser]
[SimpleJob]
public class MPSC_DisruptorBenchmarks
{
    private Disruptor<MarketDataEventClass> _disruptor = null!;
    private RingBuffer<MarketDataEventClass> _ringBuffer = null!;
    private ManualResetEventSlim _consumerSignal = null!;
    private MarketDataEventClass[] _events = null!;
    private SimpleEventHandler _eventHandler = null!;

    [Params(2, 4, 8)]
    public int ProducerCount { get; set; }

    // 소비자가 처리해야 할 총 이벤트 수
    private long _totalOperations;

    [GlobalSetup]
    public void Setup()
    {
        _events = new MarketDataEventClass[1];
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        _events[0] = new MarketDataEventClass(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            side: Side.Buy,
            priceTicks: PriceUtils.ToTicks(50000m),
            quantity: 100000000,
            kind: EventKind.Update,
            symbolId: symbolId
        );

        const int operationsPerProducer = 100_000;
        _totalOperations = operationsPerProducer * ProducerCount;

        _consumerSignal = new ManualResetEventSlim(false);

        _eventHandler = new SimpleEventHandler(_totalOperations, _consumerSignal);

        // Disruptor 인스턴스 생성
        _disruptor = new Disruptor<MarketDataEventClass>(
            () => new MarketDataEventClass(),      // 이벤트 슬롯 팩토리
            65536,                      // 링 버퍼 크기 (2의 거듭제곱)
            TaskScheduler.Default,      // 이벤트 프로세서를 실행할 스케줄러
            ProducerType.Multi,         // 다중 생산자
            new BlockingWaitStrategy()  // 소비자가 대기하는 방식
        );

        _disruptor.HandleEventsWith(_eventHandler);
        _ringBuffer = _disruptor.Start();
    }

    [Benchmark(Description = "Disruptor MPSC: Multi-threaded Write, Single-threaded Read")]
    public void MultiThread_WriteRead()
    {
        _consumerSignal.Reset();
        _eventHandler.Reset();
        const int operationsPerProducer = 100_000;

        var producers = new Task[ProducerCount];
        for (int i = 0; i < ProducerCount; i++)
        {
            producers[i] = Task.Run(() =>
            {
                for (int j = 0; j < operationsPerProducer; j++)
                {
                    long sequence = _ringBuffer.Next();
                    try
                    {
                        var slot = _ringBuffer[sequence];
                        slot.Sequence = _events[0].Sequence;
                        slot.Timestamp = _events[0].Timestamp;
                        slot.Side = _events[0].Side;
                        slot.PriceTicks = _events[0].PriceTicks;
                        slot.Quantity = _events[0].Quantity;
                        slot.Kind = _events[0].Kind;
                        slot.SymbolId = _events[0].SymbolId;
                    }
                    finally
                    {
                        _ringBuffer.Publish(sequence);
                    }
                }
            });
        }

        Task.WaitAll(producers);

        _consumerSignal.Wait();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _disruptor.Shutdown();
        _consumerSignal.Dispose();
    }
}

[MemoryDiagnoser]
[SimpleJob]
public class MPSC_Struct_DisruptorBenchmarks
{
    // --- T를 MarketDataEvent (struct)로 변경 ---
    private Disruptor<MarketDataEventSlot> _disruptor = null!;
    private RingBuffer<MarketDataEventSlot> _ringBuffer = null!;
    private ManualResetEventSlim _consumerSignal = null!;
    private MarketDataEvent[] _events = null!;
    private SimpleSlotEventHandler _eventHandler = null!;

    [Params(2, 4, 8)]
    public int ProducerCount { get; set; }

    private long _totalOperations;

    [GlobalSetup]
    public void Setup()
    {
        _events = new MarketDataEvent[1];
        var symbolId = SymbolUtils.GetSymbolId("BTCUSDT");
        var entryArr = new PriceLevelEntryArray();
        entryArr[0] = new PriceLevelEntry(Side.Buy, 50000m, 100000000m);
        _events[0] = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            kind: EventKind.Update,
            instrumentId: symbolId,
            exchange: ExchangeEnum.BINANCE,
            updateCount: 1,
            updates: entryArr
        );

        const int operationsPerProducer = 100_000;
        _totalOperations = operationsPerProducer * ProducerCount;
        _consumerSignal = new ManualResetEventSlim(false);

        // --- struct를 처리하는 이벤트 핸들러 사용 ---
        _eventHandler = new SimpleSlotEventHandler(_totalOperations, _consumerSignal);

        // --- T를 MarketDataEvent (struct)로 변경 ---
        _disruptor = new Disruptor<MarketDataEventSlot>(
            () => new MarketDataEventSlot(),      // struct는 기본 생성자가 있음
            65536,
            TaskScheduler.Default,
            ProducerType.Multi,
            new BlockingWaitStrategy()
        );

        _disruptor.HandleEventsWith(_eventHandler);
        _ringBuffer = _disruptor.Start();
    }

    [Benchmark(Description = "Disruptor MPSC Struct: Multi-threaded Write, Single-threaded Read")]
    public void MultiThread_WriteRead()
    {
        _consumerSignal.Reset();
        _eventHandler.Reset();

        const int operationsPerProducer = 100_000;
        var producers = new Task[ProducerCount];
        for (int i = 0; i < ProducerCount; i++)
        {
            producers[i] = Task.Run(() =>
            {
                var eventData = _events[0];
                for (int j = 0; j < operationsPerProducer; j++)
                {
                    long sequence = _ringBuffer.Next();
                    try
                    {
                        var slot = _ringBuffer[sequence];
                        slot.Value = eventData; // struct의 값 복사
                    }
                    finally
                    {
                        _ringBuffer.Publish(sequence);
                    }
                }
            });
        }

        Task.WaitAll(producers);
        _consumerSignal.Wait();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _disruptor.Shutdown();
        _consumerSignal.Dispose();
    }
}

[MemoryDiagnoser]
[SimpleJob]
public class TimestampBenchmarks
{
    [Benchmark]
    public long GetTimestampMicros()
    {
        return TimestampUtils.GetTimestampMicros();
    }

    [Benchmark]
    [Arguments(1000)]
    [Arguments(10000)]
    public void TimestampBatch(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var timestamp = TimestampUtils.GetTimestampMicros();
        }
    }
}

[MemoryDiagnoser]
[SimpleJob]
public class PriceUtilsBenchmarks
{
    private readonly decimal[] _prices = { 50000.1234m, 1.23456789m, 0.00001234m, 99999.9999m };
    private readonly long[] _ticks = new long[4];

    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < _prices.Length; i++)
        {
            _ticks[i] = PriceUtils.ToTicks(_prices[i]);
        }
    }

    [Benchmark]
    public long ToTicks()
    {
        return PriceUtils.ToTicks(50000.1234m);
    }

    [Benchmark]
    public decimal FromTicks()
    {
        return PriceUtils.FromTicks(500001234);
    }

    [Benchmark]
    public void PriceConversionBatch()
    {
        for (int i = 0; i < _prices.Length; i++)
        {
            var ticks = PriceUtils.ToTicks(_prices[i]);
            var price = PriceUtils.FromTicks(ticks);
        }
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        var summary = BenchmarkRunner.Run<OrderBookBenchmarks>();

        Console.WriteLine("\nPress any key to run Ring Buffer benchmarks...");
        Console.ReadKey();
        BenchmarkRunner.Run<RingBufferBenchmarks>();
        BenchmarkRunner.Run<MPSC_Struct_DisruptorBenchmarks>();
        // BenchmarkRunner.Run<MPSC_DisruptorBenchmarks>();
        // BenchmarkRunner.Run<MPSC_RingBufferBenchmarks>();
        // BenchmarkRunner.Run<MPSC_ChannelBenchmarks>();

        Console.WriteLine("\nPress any key to run Timestamp benchmarks...");
        Console.ReadKey();
        BenchmarkRunner.Run<TimestampBenchmarks>();

        Console.WriteLine("\nPress any key to run Price Utils benchmarks...");
        Console.ReadKey();
        BenchmarkRunner.Run<PriceUtilsBenchmarks>();
    }
}
