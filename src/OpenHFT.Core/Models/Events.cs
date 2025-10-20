using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpenHFT.Core.Models;

/// <summary>
/// A single price level entry containing side, price, and quantity.
/// Used within the inline array for GC-free batch updates.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct PriceLevelEntry
{
    public readonly Side Side;
    public readonly decimal PriceTicks;
    public readonly decimal Quantity;

    public PriceLevelEntry(Side side, decimal priceTicks, decimal quantity)
    {
        Side = side;
        PriceTicks = priceTicks;
        Quantity = quantity;
    }
}

/// <summary>
/// Defines an inline array of 40 PriceLevelEntry structs.
/// This prevents heap allocation inside the MarketDataEvent struct.
/// </summary>
[InlineArray(40)] // 최대 40개 레벨 업데이트를 가정
public struct PriceLevelEntryArray
{
    // C# 컴파일러가 이 구조체의 메모리 레이아웃을 PriceLevelEntry 40개 분량으로 처리합니다.
    private PriceLevelEntry _element0;
}

/// <summary>
/// High-performance market data event with minimal allocations
/// Prices and quantities are represented as long integers (ticks) to avoid decimal operations
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct MarketDataEvent
{
    public readonly long PrevSequence;  // Previous sequence number from feed
    public readonly long Sequence;      // Sequence number from feed
    public readonly long Timestamp;     // Monotonic timestamp in microseconds
    public readonly EventKind Kind;     // Add/Update/Delete/Trade
    public readonly int InstrumentId;       // Internal symbol identifier
    public readonly ExchangeEnum SourceExchange;
    public readonly int TopicId;        // Identifier for the source topic (e.g., AggTrade, DepthUpdate)
    public readonly int UpdateCount;
    public readonly PriceLevelEntryArray Updates;

    public MarketDataEvent(long sequence, long timestamp, EventKind kind, int instrumentId, ExchangeEnum exchange,
                          long prevSequence = 0, int topicId = 0, int updateCount = 0, PriceLevelEntryArray updates = default)
    {
        Sequence = sequence;
        Timestamp = timestamp;
        Kind = kind;
        InstrumentId = instrumentId;
        SourceExchange = exchange;
        PrevSequence = prevSequence;
        TopicId = topicId;
        UpdateCount = updateCount;
        Updates = updates;
    }

    public override string ToString()
    {
        var firstUpdate = Updates[0];
        return $"MD[{Sequence}] Batch({UpdateCount}) {Kind} {firstUpdate.Side} {firstUpdate.PriceTicks}@{firstUpdate.Quantity} @{Timestamp}μs";
    }
}

/// <summary>
/// A mutable class that wraps a MarketDataEvent struct for use in Disrupter-like systems
/// that require reference types.
/// This class will be pre-allocated in the RingBuffer.
/// </summary>
public class MarketDataEventWrapper // class로 정의
{
    public MarketDataEvent Event;

    public MarketDataEventWrapper()
    {
        Event = default(MarketDataEvent);
    }

    public void SetData(in MarketDataEvent data)
    {
        Event = data;
    }

    public void Clear()
    {
        Event = default(MarketDataEvent);
    }

    public override string ToString() => Event.ToString();
}

/// <summary>
/// Order intent from strategy to gateway
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct OrderIntent
{
    public readonly long ClientOrderId;
    public readonly OrderTypeEnum Type;     // Limit/Market/Stop
    public readonly Side Side;          // Buy/Sell
    public readonly long PriceTicks;    // For Limit orders
    public readonly long Quantity;
    public readonly long TimestampIn;   // Timestamp when intent was created
    public readonly int SymbolId;

    public OrderIntent(long clientOrderId, OrderTypeEnum type, Side side, long priceTicks,
                      long quantity, long timestampIn, int symbolId)
    {
        ClientOrderId = clientOrderId;
        Type = type;
        Side = side;
        PriceTicks = priceTicks;
        Quantity = quantity;
        TimestampIn = timestampIn;
        SymbolId = symbolId;
    }

    public override string ToString() =>
        $"Intent[{ClientOrderId}] {Type} {Side} {PriceTicks}@{Quantity}";
}

/// <summary>
/// Order acknowledgment from gateway/matcher
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct OrderAck
{
    public readonly long ClientOrderId;
    public readonly long ExchangeOrderId;
    public readonly AckKind Kind;       // Ack/Reject/CancelAck/ReplaceAck
    public readonly long TimestampOut; // When ack was generated
    public readonly string RejectReason;

    public OrderAck(long clientOrderId, long exchangeOrderId, AckKind kind,
                   long timestampOut, string rejectReason = "")
    {
        ClientOrderId = clientOrderId;
        ExchangeOrderId = exchangeOrderId;
        Kind = kind;
        TimestampOut = timestampOut;
        RejectReason = rejectReason;
    }

    public override string ToString() =>
        $"Ack[{ClientOrderId}→{ExchangeOrderId}] {Kind} @{TimestampOut}μs";
}

/// <summary>
/// Fill event from matching engine
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct FillEvent
{
    public readonly long ClientOrderId;
    public readonly long ExchangeOrderId;
    public readonly long PriceTicks;
    public readonly long Quantity;
    public readonly long TimestampFill;
    public readonly bool IsFullFill;

    public FillEvent(long clientOrderId, long exchangeOrderId, long priceTicks,
                    long quantity, long timestampFill, bool isFullFill)
    {
        ClientOrderId = clientOrderId;
        ExchangeOrderId = exchangeOrderId;
        PriceTicks = priceTicks;
        Quantity = quantity;
        TimestampFill = timestampFill;
        IsFullFill = isFullFill;
    }

    public override string ToString() =>
        $"Fill[{ClientOrderId}] {PriceTicks}@{Quantity} @{TimestampFill}μs {(IsFullFill ? "FULL" : "PARTIAL")}";
}
