using System.Threading.Channels;

namespace OpenHFT.Feed.Interfaces;

/// <summary>
/// Statistics for individual feed adapters(matching with product-type)
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
    // GC-Free를 위해 CircularBuffer 대신 직접 배열과 인덱스를 관리합니다.
    private readonly double[] _e2eLatencies;
    private int _latencyIndex;
    private int _latencyCount;
    private readonly object _latencyLock = new();

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
    public double AvgE2ELatency
    {
        get
        {
            lock (_latencyLock)
            {
                if (_latencyCount == 0) return 0.0;

                double sum = 0;
                // 버퍼가 꽉 차지 않았을 경우, 실제 저장된 만큼만 계산합니다.
                for (int i = 0; i < _latencyCount; i++)
                {
                    sum += _e2eLatencies[i];
                }
                return sum / _latencyCount;
            }
        }
    }
    public double MessagesPerSecond => MessagesReceived / (DateTimeOffset.UtcNow - StartTime).TotalSeconds;
    public double DropRate => MessagesReceived > 0 ? (double)MessagesDropped / MessagesReceived : 0;

    public FeedStatistics(int latencyWindowSize = 100)
    {
        StartTime = DateTimeOffset.UtcNow;
        LastConnectionTime = DateTimeOffset.UtcNow;
        _e2eLatencies = new double[latencyWindowSize];
    }

    public void RecordReconnect()
    {
        Interlocked.Increment(ref _reconnectCount);
        LastConnectionTime = DateTimeOffset.UtcNow;
    }

    public void RecordMessageReceived()
    {
        Interlocked.Increment(ref _messagesReceived);
        LastMessageTime = DateTimeOffset.UtcNow;
    }

    public void RecordMessageProcessed()
    {
        Interlocked.Increment(ref _messagesProcessed);
    }
    public void RecordMessageDropped()
    {
        Interlocked.Increment(ref _messagesDropped);
        Interlocked.Decrement(ref _messagesProcessed);
    }
    public void RecordSequenceGap() => Interlocked.Increment(ref _sequenceGaps);
    public void AddE2ELatency(double e2eLatencyMs)
    {
        lock (_latencyLock)
        {
            _e2eLatencies[_latencyIndex] = e2eLatencyMs;
            _latencyIndex = (_latencyIndex + 1) % _e2eLatencies.Length;
            if (_latencyCount < _e2eLatencies.Length)
            {
                _latencyCount++;
            }
        }
    }

    /// <summary>
    /// Calculates the latency for a given percentile.
    /// </summary>
    /// <param name="percentile">The percentile to calculate (e.g., 0.95 for 95th percentile).</param>
    /// <returns>The latency value at the specified percentile.</returns>
    public double GetLatencyPercentile(double percentile)
    {
        if (percentile < 0.0 || percentile > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be between 0.0 and 1.0.");
        }

        // ToArray() 호출로 인한 힙 할당을 피하기 위해 스택에 임시 배열을 만듭니다.
        // Span<T>를 사용하여 힙 할당 없이 배열의 일부를 다룰 수 있습니다.
        Span<double> tempSpan = stackalloc double[_latencyCount];

        lock (_latencyLock)
        {
            if (_latencyCount == 0) return 0.0;

            // _e2eLatencies의 유효한 데이터만 스택의 tempSpan으로 복사합니다.
            new Span<double>(_e2eLatencies, 0, _latencyCount).CopyTo(tempSpan);
        }

        tempSpan.Sort();

        int index = (int)Math.Ceiling(percentile * _latencyCount) - 1;
        return tempSpan[Math.Max(0, index)];
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
