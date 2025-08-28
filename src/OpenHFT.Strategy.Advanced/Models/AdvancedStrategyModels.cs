using OpenHFT.Core.Models;
using OpenHFT.Book.Core;

namespace OpenHFT.Strategy.Advanced;

/// <summary>
/// Interface for advanced trading strategies with sophisticated features
/// </summary>
public interface IAdvancedStrategy
{
    string Name { get; }
    AdvancedStrategyState State { get; }
    
    Task<List<OrderIntent>> ProcessMarketData(MarketDataEvent marketData, OrderBook orderBook);
    Task StartAsync();
    Task StopAsync();
    StrategyStatistics GetStatistics();
}

/// <summary>
/// Enhanced strategy state enumeration
/// </summary>
public enum AdvancedStrategyState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

/// <summary>
/// Comprehensive strategy statistics
/// </summary>
public class StrategyStatistics
{
    public string StrategyName { get; set; } = string.Empty;
    public decimal TotalPnL { get; set; }
    public decimal SharpeRatio { get; set; }
    public decimal Sharpe { get; set; } // For backward compatibility
    public decimal MaxDrawdown { get; set; }
    public decimal WinRate { get; set; }
    public decimal SuccessRate { get; set; } // For backward compatibility
    public long TotalTrades { get; set; }
    public long TotalSignals { get; set; }
    public long ExecutedSignals { get; set; }
    public long ActivePositions { get; set; }
    public long WinningTrades { get; set; }
    public long LosingTrades { get; set; }
    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }
    public decimal ProfitFactor { get; set; }
    public TimeSpan AverageHoldTime { get; set; }
    public decimal VolumeTraded { get; set; }
    public DateTime LastUpdate { get; set; }
    public Dictionary<string, decimal> SymbolPnL { get; set; } = new();
    public List<TradeResult> RecentTrades { get; set; } = new();
    public bool IsEnabled { get; set; }
    public string Status { get; set; } = "Stopped";
    public decimal PnL => TotalPnL; // For backward compatibility
}

/// <summary>
/// Individual trade result
/// </summary>
public class TradeResult
{
    public long TradeId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public DateTime OpenTime { get; set; }
    public DateTime CloseTime { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal PnL { get; set; }
    public string Side { get; set; } = string.Empty;
    public string Strategy { get; set; } = string.Empty;
}

/// <summary>
/// Utility class for timestamp operations
/// </summary>
public static class TimestampUtils
{
    private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    
    public static long GetTimestampMicros()
    {
        return (long)((DateTime.UtcNow - UnixEpoch).TotalMicroseconds);
    }
    
    public static DateTime FromTimestampMicros(long timestampMicros)
    {
        return UnixEpoch.AddMicroseconds(timestampMicros);
    }
    
    public static long GetTimestampMillis()
    {
        return (long)((DateTime.UtcNow - UnixEpoch).TotalMilliseconds);
    }
}
