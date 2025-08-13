using System.Runtime.CompilerServices;
using System.Diagnostics;

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
