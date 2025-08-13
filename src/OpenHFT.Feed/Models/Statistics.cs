using System.Threading.Channels;

namespace OpenHFT.Feed.Interfaces;

/// <summary>
/// Statistics for individual feed adapters
/// </summary>
public class FeedStatistics
{
    public long MessagesReceived { get; set; }
    public long MessagesProcessed { get; set; }
    public long MessagesDropped { get; set; }
    public long BytesReceived { get; set; }
    public long ReconnectCount { get; set; }
    public long SequenceGaps { get; set; }
    public DateTimeOffset LastMessageTime { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public TimeSpan Uptime => DateTimeOffset.UtcNow - StartTime;

    public double MessagesPerSecond
    {
        get
        {
            var elapsed = Uptime.TotalSeconds;
            return elapsed > 0 ? MessagesReceived / elapsed : 0;
        }
    }

    public double DropRate
    {
        get
        {
            return MessagesReceived > 0 ? (double)MessagesDropped / MessagesReceived : 0;
        }
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
