using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Core.Collections;
using OpenHFT.Book.Core;

namespace OpenHFT.Benchmarks;

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
        _orderBook = new OrderBook("BTCUSDT");
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
            
            _events[i] = new MarketDataEvent(
                sequence: i + 1,
                timestamp: TimestampUtils.GetTimestampMicros(),
                side: side,
                priceTicks: PriceUtils.ToTicks((decimal)price),
                quantity: quantity,
                kind: EventKind.Update,
                symbolId: _symbolId
            );
        }
    }

    [Benchmark]
    public void ApplySingleEvent()
    {
        var evt = new MarketDataEvent(
            sequence: 1,
            timestamp: TimestampUtils.GetTimestampMicros(),
            side: Side.Buy,
            priceTicks: PriceUtils.ToTicks(50000m),
            quantity: 100000000,
            kind: EventKind.Update,
            symbolId: _symbolId
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
        var spread = _orderBook.GetSpreadTicks();
        var mid = _orderBook.GetMidPriceTicks();
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
            _events[i] = new MarketDataEvent(
                sequence: i + 1,
                timestamp: TimestampUtils.GetTimestampMicros(),
                side: Side.Buy,
                priceTicks: PriceUtils.ToTicks(50000m),
                quantity: 100000000,
                kind: EventKind.Update,
                symbolId: symbolId
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
        
        Console.WriteLine("\nPress any key to run Timestamp benchmarks...");
        Console.ReadKey();
        BenchmarkRunner.Run<TimestampBenchmarks>();
        
        Console.WriteLine("\nPress any key to run Price Utils benchmarks...");
        Console.ReadKey();
        BenchmarkRunner.Run<PriceUtilsBenchmarks>();
    }
}
