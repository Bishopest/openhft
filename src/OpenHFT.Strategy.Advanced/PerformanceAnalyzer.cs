using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Strategy.Interfaces;
using MathNet.Numerics.Statistics;
using System.Collections.Concurrent;

namespace OpenHFT.Strategy.Advanced;

/// <summary>
/// Advanced performance analytics engine for HFT strategies with real-time metrics,
/// statistical analysis, and machine learning-inspired performance prediction.
/// </summary>
public class PerformanceAnalyzer
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, StrategyPerformance> _strategyPerformance;
    private readonly ConcurrentQueue<PerformanceEvent> _eventHistory;
    private readonly ConcurrentDictionary<string, PerformanceModel> _performanceModels;
    
    // Analytics configuration
    private readonly int _maxEventHistory = 10000;
    private readonly int _performanceWindow = 1000;
    private readonly TimeSpan _realtimeUpdateInterval = TimeSpan.FromSeconds(1);
    
    // Real-time tracking
    private DateTime _lastUpdateTime = DateTime.UtcNow;
    private readonly object _analyticsLock = new();
    
    public PerformanceAnalyzer(ILogger logger)
    {
        _logger = logger;
        _strategyPerformance = new ConcurrentDictionary<string, StrategyPerformance>();
        _eventHistory = new ConcurrentQueue<PerformanceEvent>();
        _performanceModels = new ConcurrentDictionary<string, PerformanceModel>();
    }
    
    /// <summary>
    /// Process a strategy event for performance tracking
    /// </summary>
    public async Task ProcessEvent(StrategyEvent strategyEvent)
    {
        var perfEvent = new PerformanceEvent
        {
            StrategyName = strategyEvent.StrategyName,
            EventType = strategyEvent.EventType,
            Timestamp = strategyEvent.Timestamp,
            SymbolId = strategyEvent.SymbolId,
            OrderCount = strategyEvent.OrderCount
        };
        
        // Add to event history
        _eventHistory.Enqueue(perfEvent);
        
        // Maintain event history size
        while (_eventHistory.Count > _maxEventHistory && _eventHistory.TryDequeue(out _)) { }
        
        // Update strategy performance
        var performance = GetOrCreateStrategyPerformance(strategyEvent.StrategyName);
        await UpdateStrategyPerformance(performance, perfEvent);
        
        // Update performance model
        await UpdatePerformanceModel(strategyEvent.StrategyName, perfEvent);
    }
    
    /// <summary>
    /// Update strategy metrics with latest statistics
    /// </summary>
    public async Task UpdateStrategyMetrics(string strategyName, StrategyStatistics stats)
    {
        var performance = GetOrCreateStrategyPerformance(strategyName);
        
        lock (performance.UpdateLock)
        {
            // Update current metrics
            performance.CurrentStats = stats;
            performance.LastUpdateTime = TimestampUtils.GetTimestampMicros();
            
            // Add to historical data
            performance.PnLHistory.Add(new PnLPoint
            {
                Timestamp = performance.LastUpdateTime,
                PnL = stats.TotalPnL,
                Sharpe = stats.Sharpe,
                Drawdown = stats.MaxDrawdown
            });
            
            // Maintain rolling window
            if (performance.PnLHistory.Count > _performanceWindow)
            {
                performance.PnLHistory.RemoveAt(0);
            }
            
            // Update derived metrics
            UpdateDerivedMetrics(performance);
        }
    }
    
    /// <summary>
    /// Get comprehensive analytics for all strategies
    /// </summary>
    public async Task<PerformanceAnalytics> GetAnalytics()
    {
        var analytics = new PerformanceAnalytics
        {
            GeneratedAt = TimestampUtils.GetTimestampMicros(),
            AnalysisWindow = _performanceWindow,
            TotalStrategies = _strategyPerformance.Count
        };
        
        // Aggregate strategy analytics
        var allPerformances = _strategyPerformance.Values.ToList();
        
        if (allPerformances.Any())
        {
            analytics.AverageReturn = allPerformances.Average(p => p.AverageReturn);
            analytics.AverageSharpe = allPerformances.Average(p => p.CurrentStats?.Sharpe ?? 0m);
            analytics.TotalVolume = allPerformances.Sum(p => p.TotalVolume);
            analytics.TotalTrades = allPerformances.Sum(p => p.TotalTrades);
            analytics.AverageWinRate = allPerformances.Average(p => p.WinRate);
            analytics.MaxDrawdown = allPerformances.Max(p => p.MaxDrawdown);
            
            // Calculate correlation matrix
            analytics.StrategyCorrelations = CalculateStrategyCorrelations(allPerformances);
            
            // Risk-adjusted metrics
            analytics.SortinoRatio = CalculatePortfolioSortino(allPerformances);
            analytics.CalmarRatio = analytics.AverageReturn / (analytics.MaxDrawdown > 0 ? analytics.MaxDrawdown : 0.01m);
            
            // Performance prediction
            analytics.PerformancePredictions = GeneratePerformancePredictions();
        }
        
        // Market regime analysis
        analytics.MarketRegime = AnalyzeMarketRegime();
        
        // Strategy rankings
        analytics.StrategyRankings = RankStrategies(allPerformances);
        
        return analytics;
    }
    
    /// <summary>
    /// Get performance analytics for a specific strategy
    /// </summary>
    public StrategyAnalytics? GetStrategyAnalytics(string strategyName)
    {
        if (!_strategyPerformance.TryGetValue(strategyName, out var performance))
            return null;
        
        lock (performance.UpdateLock)
        {
            var analytics = new StrategyAnalytics
            {
                StrategyName = strategyName,
                CurrentStats = performance.CurrentStats,
                AverageReturn = performance.AverageReturn,
                VolatilityAnnualized = performance.VolatilityAnnualized,
                SharpeRatio = performance.SharpeRatio,
                SortinoRatio = performance.SortinoRatio,
                MaxDrawdown = performance.MaxDrawdown,
                WinRate = performance.WinRate,
                ProfitFactor = performance.ProfitFactor,
                AverageWin = performance.AverageWin,
                AverageLoss = performance.AverageLoss,
                TotalTrades = performance.TotalTrades,
                LastUpdateTime = performance.LastUpdateTime
            };
            
            // Recent performance (last 100 data points)
            var recentPnL = performance.PnLHistory.TakeLast(100).ToList();
            if (recentPnL.Count > 1)
            {
                analytics.RecentPerformance = new RecentPerformance
                {
                    Period = "Last 100 trades",
                    Returns = recentPnL.Select(p => p.PnL).ToList(),
                    AverageReturn = recentPnL.Average(p => p.PnL),
                    Volatility = CalculateVolatility(recentPnL.Select(p => p.PnL).ToList()),
                    SharpeRatio = recentPnL.Average(p => p.Sharpe)
                };
            }
            
            // Performance model predictions
            if (_performanceModels.TryGetValue(strategyName, out var model))
            {
                analytics.PerformancePrediction = model.PredictNextPeriod();
            }
            
            return analytics;
        }
    }
    
    /// <summary>
    /// Update derived performance metrics
    /// </summary>
    private void UpdateDerivedMetrics(StrategyPerformance performance)
    {
        if (performance.PnLHistory.Count < 10)
            return;
        
        var returns = performance.PnLHistory.Select(p => p.PnL).ToList();
        var recentReturns = returns.TakeLast(Math.Min(100, returns.Count)).ToList();
        
        // Calculate volatility (annualized)
        performance.VolatilityAnnualized = CalculateVolatility(recentReturns) * (decimal)Math.Sqrt(252); // 252 trading days
        
        // Calculate Sharpe ratio
        var avgReturn = recentReturns.Average();
        var riskFreeRate = 0.02m / 252m; // 2% annual risk-free rate, daily
        performance.SharpeRatio = performance.VolatilityAnnualized > 0 ? 
            (avgReturn - riskFreeRate) / performance.VolatilityAnnualized : 0m;
        
        // Calculate Sortino ratio (only downside volatility)
        var downsideReturns = recentReturns.Where(r => r < 0).ToList();
        var downsideVolatility = downsideReturns.Count > 0 ? CalculateVolatility(downsideReturns) : 0.01m;
        performance.SortinoRatio = (avgReturn - riskFreeRate) / downsideVolatility;
        
        // Calculate average return
        performance.AverageReturn = avgReturn;
        
        // Calculate max drawdown
        performance.MaxDrawdown = CalculateMaxDrawdown(returns);
        
        // Calculate win/loss metrics
        var wins = recentReturns.Where(r => r > 0).ToList();
        var losses = recentReturns.Where(r => r < 0).ToList();
        
        performance.WinRate = recentReturns.Count > 0 ? (decimal)wins.Count / recentReturns.Count : 0m;
        performance.AverageWin = wins.Count > 0 ? wins.Average() : 0m;
        performance.AverageLoss = losses.Count > 0 ? Math.Abs(losses.Average()) : 0m;
        performance.ProfitFactor = performance.AverageLoss > 0 ? 
            (performance.AverageWin * wins.Count) / (performance.AverageLoss * losses.Count) : 0m;
        
        performance.TotalTrades = recentReturns.Count;
    }
    
    /// <summary>
    /// Update performance model for prediction
    /// </summary>
    private async Task UpdatePerformanceModel(string strategyName, PerformanceEvent perfEvent)
    {
        var model = _performanceModels.GetOrAdd(strategyName, _ => new PerformanceModel(strategyName));
        
        await model.UpdateModel(perfEvent);
    }
    
    /// <summary>
    /// Calculate strategy correlation matrix
    /// </summary>
    private Dictionary<string, Dictionary<string, decimal>> CalculateStrategyCorrelations(
        List<StrategyPerformance> performances)
    {
        var correlations = new Dictionary<string, Dictionary<string, decimal>>();
        
        foreach (var perf1 in performances)
        {
            if (perf1.PnLHistory.Count < 20) continue;
            
            correlations[perf1.StrategyName] = new Dictionary<string, decimal>();
            
            foreach (var perf2 in performances)
            {
                if (perf2.PnLHistory.Count < 20) continue;
                
                var correlation = CalculateCorrelation(
                    perf1.PnLHistory.Select(p => p.PnL).ToList(),
                    perf2.PnLHistory.Select(p => p.PnL).ToList()
                );
                
                correlations[perf1.StrategyName][perf2.StrategyName] = correlation;
            }
        }
        
        return correlations;
    }
    
    /// <summary>
    /// Calculate portfolio Sortino ratio
    /// </summary>
    private decimal CalculatePortfolioSortino(List<StrategyPerformance> performances)
    {
        var allReturns = new List<decimal>();
        
        foreach (var perf in performances)
        {
            allReturns.AddRange(perf.PnLHistory.Select(p => p.PnL));
        }
        
        if (allReturns.Count == 0) return 0m;
        
        var avgReturn = allReturns.Average();
        var riskFreeRate = 0.02m / 252m;
        var downsideReturns = allReturns.Where(r => r < 0).ToList();
        var downsideVolatility = downsideReturns.Count > 0 ? CalculateVolatility(downsideReturns) : 0.01m;
        
        return (avgReturn - riskFreeRate) / downsideVolatility;
    }
    
    /// <summary>
    /// Generate performance predictions using simple models
    /// </summary>
    private Dictionary<string, PerformancePrediction> GeneratePerformancePredictions()
    {
        var predictions = new Dictionary<string, PerformancePrediction>();
        
        foreach (var kvp in _performanceModels)
        {
            var strategyName = kvp.Key;
            var model = kvp.Value;
            
            predictions[strategyName] = model.PredictNextPeriod();
        }
        
        return predictions;
    }
    
    /// <summary>
    /// Analyze current market regime
    /// </summary>
    private MarketRegime AnalyzeMarketRegime()
    {
        // Simplified market regime analysis
        var recentEvents = _eventHistory.Where(e => 
            TimestampUtils.GetTimestampMicros() - e.Timestamp < TimeSpan.FromHours(1).Ticks * 10).ToList();
        
        if (recentEvents.Count == 0)
            return MarketRegime.Normal;
        
        var orderGenerationRate = recentEvents.Count(e => e.EventType == StrategyEventType.OrderGenerated);
        var orderExecutionRate = recentEvents.Count(e => e.EventType == StrategyEventType.OrderExecuted);
        
        // High activity = trending market, low activity = ranging market
        if (orderGenerationRate > 100)
            return MarketRegime.HighVolatility;
        else if (orderGenerationRate < 20)
            return MarketRegime.LowVolatility;
        else
            return MarketRegime.Normal;
    }
    
    /// <summary>
    /// Rank strategies by performance
    /// </summary>
    private List<StrategyRanking> RankStrategies(List<StrategyPerformance> performances)
    {
        return performances
            .Where(p => p.CurrentStats != null)
            .Select(p => new StrategyRanking
            {
                StrategyName = p.StrategyName,
                Score = CalculateStrategyScore(p),
                Rank = 0, // Will be set after sorting
                SharpeRatio = p.SharpeRatio,
                TotalReturn = p.AverageReturn * p.TotalTrades,
                MaxDrawdown = p.MaxDrawdown,
                WinRate = p.WinRate
            })
            .OrderByDescending(r => r.Score)
            .Select((r, index) => { r.Rank = index + 1; return r; })
            .ToList();
    }
    
    /// <summary>
    /// Calculate composite strategy score
    /// </summary>
    private decimal CalculateStrategyScore(StrategyPerformance performance)
    {
        // Weighted scoring system
        var sharpeWeight = 0.3m;
        var returnWeight = 0.3m;
        var drawdownWeight = 0.2m;
        var winRateWeight = 0.2m;
        
        var sharpeScore = Math.Max(0, Math.Min(10, performance.SharpeRatio * 2)); // 0-10 scale
        var returnScore = Math.Max(0, Math.Min(10, performance.AverageReturn * 1000)); // 0-10 scale
        var drawdownScore = Math.Max(0, 10 - (performance.MaxDrawdown * 100)); // Lower drawdown = higher score
        var winRateScore = performance.WinRate * 10; // 0-10 scale
        
        return sharpeScore * sharpeWeight + 
               returnScore * returnWeight + 
               drawdownScore * drawdownWeight + 
               winRateScore * winRateWeight;
    }
    
    /// <summary>
    /// Update individual strategy performance
    /// </summary>
    private async Task UpdateStrategyPerformance(StrategyPerformance performance, PerformanceEvent perfEvent)
    {
        lock (performance.UpdateLock)
        {
            performance.TotalEvents++;
            
            if (perfEvent.EventType == StrategyEventType.OrderGenerated)
            {
                performance.TotalVolume += perfEvent.OrderCount;
            }
            
            performance.LastEventTime = perfEvent.Timestamp;
        }
    }
    
    // Helper methods
    private StrategyPerformance GetOrCreateStrategyPerformance(string strategyName)
    {
        return _strategyPerformance.GetOrAdd(strategyName, _ => new StrategyPerformance(strategyName));
    }
    
    private decimal CalculateVolatility(List<decimal> returns)
    {
        if (returns.Count < 2) return 0m;
        
        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / returns.Count;
        return (decimal)Math.Sqrt((double)variance);
    }
    
    private decimal CalculateMaxDrawdown(List<decimal> returns)
    {
        if (returns.Count == 0) return 0m;
        
        decimal maxDrawdown = 0m;
        decimal peak = returns[0];
        decimal cumulativeReturn = 0m;
        
        foreach (var return_ in returns)
        {
            cumulativeReturn += return_;
            peak = Math.Max(peak, cumulativeReturn);
            var drawdown = (peak - cumulativeReturn) / Math.Max(peak, 0.01m);
            maxDrawdown = Math.Max(maxDrawdown, drawdown);
        }
        
        return maxDrawdown;
    }
    
    private decimal CalculateCorrelation(List<decimal> series1, List<decimal> series2)
    {
        var minLength = Math.Min(series1.Count, series2.Count);
        if (minLength < 10) return 0m;
        
        var x = series1.TakeLast(minLength).Select(v => (double)v).ToArray();
        var y = series2.TakeLast(minLength).Select(v => (double)v).ToArray();
        
        try
        {
            var correlation = Correlation.Pearson(x, y);
            
            // Handle special cases that can't be converted to decimal
            if (double.IsNaN(correlation) || double.IsInfinity(correlation) || Math.Abs(correlation) > (double)decimal.MaxValue)
            {
                return 0m;
            }
            
            return (decimal)correlation;
        }
        catch (OverflowException)
        {
            return 0m;
        }
    }
}

// Supporting classes
public class StrategyPerformance
{
    public string StrategyName { get; }
    public StrategyStatistics? CurrentStats { get; set; }
    public List<PnLPoint> PnLHistory { get; } = new();
    public long TotalEvents { get; set; }
    public long TotalVolume { get; set; }
    public long TotalTrades { get; set; }
    public long LastEventTime { get; set; }
    public long LastUpdateTime { get; set; }
    
    // Derived metrics
    public decimal AverageReturn { get; set; }
    public decimal VolatilityAnnualized { get; set; }
    public decimal SharpeRatio { get; set; }
    public decimal SortinoRatio { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal WinRate { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }
    
    public readonly object UpdateLock = new();
    
    public StrategyPerformance(string strategyName)
    {
        StrategyName = strategyName;
    }
}

public class PnLPoint
{
    public long Timestamp { get; set; }
    public decimal PnL { get; set; }
    public decimal Sharpe { get; set; }
    public decimal Drawdown { get; set; }
}

public class PerformanceEvent
{
    public required string StrategyName { get; set; }
    public StrategyEventType EventType { get; set; }
    public long Timestamp { get; set; }
    public int SymbolId { get; set; }
    public int OrderCount { get; set; }
}

public class PerformanceModel
{
    public string StrategyName { get; }
    private readonly List<decimal> _recentPerformance = new();
    private readonly int _maxHistory = 100;
    
    public PerformanceModel(string strategyName)
    {
        StrategyName = strategyName;
    }
    
    public async Task UpdateModel(PerformanceEvent perfEvent)
    {
        // Simplified model update - in reality this would use proper ML
        var performanceScore = perfEvent.EventType switch
        {
            StrategyEventType.OrderExecuted => 1.0m,
            StrategyEventType.OrderGenerated => 0.5m,
            StrategyEventType.OrderRejected => -0.5m,
            _ => 0m
        };
        
        _recentPerformance.Add(performanceScore);
        
        if (_recentPerformance.Count > _maxHistory)
        {
            _recentPerformance.RemoveAt(0);
        }
    }
    
    public PerformancePrediction PredictNextPeriod()
    {
        if (_recentPerformance.Count < 10)
        {
            return new PerformancePrediction
            {
                ExpectedReturn = 0m,
                Confidence = 0m,
                RiskLevel = RiskLevel.Unknown
            };
        }
        
        // Simple moving average prediction
        var recentAvg = _recentPerformance.TakeLast(10).Average();
        var historicalAvg = _recentPerformance.Average();
        var volatility = CalculateVolatility(_recentPerformance);
        
        return new PerformancePrediction
        {
            ExpectedReturn = recentAvg,
            Confidence = Math.Max(0m, 1m - volatility),
            RiskLevel = volatility switch
            {
                > 0.5m => RiskLevel.High,
                > 0.2m => RiskLevel.Medium,
                _ => RiskLevel.Low
            }
        };
    }
    
    private decimal CalculateVolatility(List<decimal> values)
    {
        if (values.Count < 2) return 0m;
        
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return (decimal)Math.Sqrt((double)variance);
    }
}

// Result classes
public class PerformanceAnalytics
{
    public long GeneratedAt { get; set; }
    public int AnalysisWindow { get; set; }
    public int TotalStrategies { get; set; }
    public decimal AverageReturn { get; set; }
    public decimal AverageSharpe { get; set; }
    public long TotalVolume { get; set; }
    public long TotalTrades { get; set; }
    public decimal AverageWinRate { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal SortinoRatio { get; set; }
    public decimal CalmarRatio { get; set; }
    public Dictionary<string, Dictionary<string, decimal>>? StrategyCorrelations { get; set; }
    public Dictionary<string, PerformancePrediction>? PerformancePredictions { get; set; }
    public MarketRegime MarketRegime { get; set; }
    public List<StrategyRanking>? StrategyRankings { get; set; }
}

public class StrategyAnalytics
{
    public required string StrategyName { get; set; }
    public StrategyStatistics? CurrentStats { get; set; }
    public decimal AverageReturn { get; set; }
    public decimal VolatilityAnnualized { get; set; }
    public decimal SharpeRatio { get; set; }
    public decimal SortinoRatio { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal WinRate { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }
    public long TotalTrades { get; set; }
    public long LastUpdateTime { get; set; }
    public RecentPerformance? RecentPerformance { get; set; }
    public PerformancePrediction? PerformancePrediction { get; set; }
}

public class RecentPerformance
{
    public required string Period { get; set; }
    public required List<decimal> Returns { get; set; }
    public decimal AverageReturn { get; set; }
    public decimal Volatility { get; set; }
    public decimal SharpeRatio { get; set; }
}

public class PerformancePrediction
{
    public decimal ExpectedReturn { get; set; }
    public decimal Confidence { get; set; }
    public RiskLevel RiskLevel { get; set; }
}

public class StrategyRanking
{
    public required string StrategyName { get; set; }
    public decimal Score { get; set; }
    public int Rank { get; set; }
    public decimal SharpeRatio { get; set; }
    public decimal TotalReturn { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal WinRate { get; set; }
}

public enum MarketRegime
{
    LowVolatility,
    Normal,
    HighVolatility,
    Crisis
}

public enum RiskLevel
{
    Unknown,
    Low,
    Medium,
    High
}
