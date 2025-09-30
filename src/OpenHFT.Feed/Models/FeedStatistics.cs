using System.Threading.Channels;
using OpenHFT.Core.Collections;

namespace OpenHFT.Feed.Interfaces;

/// <summary>
/// Statistics for individual feed adapters
/// </summary>
public class FeedStatistics
{
    // --- atomic counter
    private long _messagesReceived;
    private long _messagesProcessed;
    private long _messagesDropped;
    private long _reconnectCount;
    private long _sequenceGaps;

    // --- latency collection ---
    private readonly CircularBuffer<double> _e2eLatencies;

    // --- time attribute ---
    public DateTimeOffset StartTime { get; }
    public DateTimeOffset LastConnectionTime { get; private set; }
    public DateTimeOffset LastMessageTime { get; private set; }

    // --- public properties --
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);
    public long MessagesProcessed => Interlocked.Read(ref _messagesProcessed);
    public long MessagesDropped => Interlocked.Read(ref _messagesDropped);
    public long ReconnectCount => Interlocked.Read(ref _reconnectCount);
    public long SequenceGaps => Interlocked.Read(ref _sequenceGaps);
    public TimeSpan ConnectionUptime => DateTimeOffset.UtcNow - LastConnectionTime;
    public double AvgE2ELatency => _e2eLatencies.Average;
    public double MessagesPerSecond => MessagesReceived / (DateTimeOffset.UtcNow - StartTime).TotalSeconds;
    public double DropRate => MessagesReceived > 0 ? (double)MessagesDropped / MessagesReceived : 0;

    public FeedStatistics(int latencyWindowSize = 100)
    {
        StartTime = DateTimeOffset.UtcNow;
        LastConnectionTime = DateTimeOffset.UtcNow;
        _e2eLatencies = new CircularBuffer<double>(latencyWindowSize);
    }

    public void RecordReconnect()
    {
        Interlocked.Increment(ref _reconnectCount);
        LastConnectionTime = DateTimeOffset.UtcNow;
    }

    public void RecordMessageProcessed()
    {
        Interlocked.Increment(ref _messagesProcessed);
        LastMessageTime = DateTimeOffset.UtcNow;
    }
    public void RecordMessageDropped() => Interlocked.Increment(ref _messagesDropped);
    public void RecordSequenceGap() => Interlocked.Increment(ref _sequenceGaps);
    public void AddE2ELatency(double e2eLatencyMs)
    {
        _e2eLatencies.Add(e2eLatencyMs);
    }
}

/// <summary>
/// Statistics for the feed handler
/// </summary>
public class FeedHandlerStatistics
{
    public long TotalEventsProcessed { get; set; }
    public long TotalEventsPublished { get; set; }
    public long TotalEventsDropped { get; set; }
    public long NormalizationErrors { get; set; }
    public DateTimeOffset StartTime { get; set; }

    private readonly Dictionary<string, long> _eventsBySymbol = new();
    private readonly Dictionary<string, long> _eventsByType = new();

    public IReadOnlyDictionary<string, long> EventsBySymbol => _eventsBySymbol;
    public IReadOnlyDictionary<string, long> EventsByType => _eventsByType;

    public void IncrementSymbolCount(string symbol)
    {
        lock (_eventsBySymbol)
        {
            _eventsBySymbol.TryGetValue(symbol, out long count);
            _eventsBySymbol[symbol] = count + 1;
        }
    }

    public void IncrementEventType(string eventType)
    {
        lock (_eventsByType)
        {
            _eventsByType.TryGetValue(eventType, out long count);
            _eventsByType[eventType] = count + 1;
        }
    }

    public double EventsPerSecond
    {
        get
        {
            var elapsed = (DateTimeOffset.UtcNow - StartTime).TotalSeconds;
            return elapsed > 0 ? TotalEventsProcessed / elapsed : 0;
        }
    }

    public double PublishRate
    {
        get
        {
            return TotalEventsProcessed > 0 ? (double)TotalEventsPublished / TotalEventsProcessed : 0;
        }
    }
}
