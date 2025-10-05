using System.Runtime.CompilerServices;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OpenHFT.Core.Utils;

/// <summary>
/// High-performance timestamp utilities using QueryPerformanceCounter
/// Provides microsecond precision for latency measurements
/// </summary>
public static class TimestampUtils
{
    private static readonly long TicksPerMicrosecond;
    private static readonly double MicrosecondsPerTick;

    static TimestampUtils()
    {
        Stopwatch.Frequency.ToString(); // Ensure static initialization
        TicksPerMicrosecond = Stopwatch.Frequency / 1_000_000L;
        MicrosecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// Get current timestamp in microseconds since system start
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetTimestampMicros()
    {
        return (long)(Stopwatch.GetTimestamp() * MicrosecondsPerTick);
    }

    /// <summary>
    /// Convert Stopwatch ticks to microseconds
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long TicksToMicros(long ticks)
    {
        return (long)(ticks * MicrosecondsPerTick);
    }

    /// <summary>
    /// Convert microseconds to Stopwatch ticks
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long MicrosToTicks(long micros)
    {
        return micros * TicksPerMicrosecond;
    }

    /// <summary>
    /// Calculate elapsed microseconds between two timestamps
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ElapsedMicros(long startTimestamp, long endTimestamp)
    {
        return endTimestamp - startTimestamp;
    }
}

/// <summary>
/// Manages time synchronization with an external server to ensure accurate latency measurements.
/// This class calculates the offset between the local clock and the server clock.
/// </summary>
public static class TimeSync
{
    // The offset between the server's clock and the local UTC clock, in microseconds.
    // Calculated as: server_time_micros - local_utc_time_micros
    private static long _timeOffsetMicros = 0;
    private static ILogger? _logger;

    public static void Initialize(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Updates the time offset based on the server's time and the current local UTC time.
    /// This should be called periodically to account for clock drift.
    /// </summary>
    /// <param name="serverTimeMillis">The server time in Unix milliseconds.</param>
    public static void UpdateTimeOffset(long serverTimeMillis)
    {
        var localTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var newOffsetMillis = serverTimeMillis - localTimeMillis;

        // Store the offset in microseconds for internal use.
        Interlocked.Exchange(ref _timeOffsetMicros, newOffsetMillis * 1000);

        _logger?.LogInformationWithCaller($"Time synchronized with server. Wall clock offset: {newOffsetMillis} ms");
    }

    /// <summary>
    /// Gets the current UTC timestamp, adjusted by the server time offset, in microseconds.
    /// This provides a timestamp that is synchronized with the server's clock.
    /// </summary>
    /// <returns>A synchronized UTC timestamp in microseconds since the Unix epoch.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetSyncedTimestampMicros()
    {
        // Get current local UTC time in microseconds and apply the offset.
        long localUtcMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        return localUtcMicros + Interlocked.Read(ref _timeOffsetMicros);
    }
}

/// <summary>
/// Price utilities for tick-based pricing
/// </summary>
public static class PriceUtils
{
    public const int DefaultTickScale = 10000; // 4 decimal places

    /// <summary>
    /// Convert decimal price to ticks
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToTicks(decimal price, int scale = DefaultTickScale)
    {
        return (long)(price * scale);
    }

    /// <summary>
    /// Convert ticks to decimal price
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal FromTicks(long ticks, int scale = DefaultTickScale)
    {
        return (decimal)ticks / scale;
    }

    /// <summary>
    /// Calculate spread in ticks
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long SpreadTicks(long bidTicks, long askTicks)
    {
        return askTicks - bidTicks;
    }

    /// <summary>
    /// Calculate mid price in ticks
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long MidTicks(long bidTicks, long askTicks)
    {
        return (bidTicks + askTicks) / 2;
    }
}

/// <summary>
/// Symbol mapping utilities
/// </summary>
public static class SymbolUtils
{
    private static readonly Dictionary<string, int> SymbolToId = new();
    private static readonly Dictionary<int, string> IdToSymbol = new();
    private static int _nextId = 1;

    /// <summary>
    /// Get or create symbol ID for a symbol string
    /// </summary>
    public static int GetSymbolId(string symbol)
    {
        if (SymbolToId.TryGetValue(symbol, out int id))
            return id;

        lock (SymbolToId)
        {
            if (SymbolToId.TryGetValue(symbol, out id))
                return id;

            id = _nextId++;
            SymbolToId[symbol] = id;
            IdToSymbol[id] = symbol;
            return id;
        }
    }

    /// <summary>
    /// Get symbol string from ID
    /// </summary>
    public static string GetSymbol(int symbolId)
    {
        return IdToSymbol.TryGetValue(symbolId, out string? symbol) ? symbol : $"UNKNOWN_{symbolId}";
    }
}
