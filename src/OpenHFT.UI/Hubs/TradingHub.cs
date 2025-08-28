using Microsoft.AspNetCore.SignalR;
using OpenHFT.Strategy.Advanced;
using OpenHFT.Core.Models;

namespace OpenHFT.UI.Hubs;

/// <summary>
/// SignalR Hub for real-time communication with the trading dashboard
/// Provides live updates for market data, strategy performance, and system metrics
/// </summary>
public class TradingHub : Hub
{
    private readonly ILogger<TradingHub> _logger;
    private readonly IAdvancedStrategyManager? _strategyManager;

    public TradingHub(ILogger<TradingHub> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        // Get strategy manager if available (optional dependency)
        _strategyManager = serviceProvider.GetService<IAdvancedStrategyManager>();
    }

    /// <summary>
    /// Called when a client connects to the hub
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client {ConnectionId} connected to TradingHub", Context.ConnectionId);
        
        // Add client to general updates group
        await Groups.AddToGroupAsync(Context.ConnectionId, "TradingUpdates");
        
        // Send initial data to newly connected client
        await SendInitialData();
        
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Called when a client disconnects from the hub
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected from TradingHub", Context.ConnectionId);
        
        if (exception != null)
        {
            _logger.LogWarning(exception, "Client disconnected with exception");
        }
        
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "TradingUpdates");
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to specific strategy updates
    /// </summary>
    public async Task SubscribeToStrategy(string strategyName)
    {
        var groupName = $"Strategy_{strategyName}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        
        _logger.LogDebug("Client {ConnectionId} subscribed to strategy {StrategyName}", 
            Context.ConnectionId, strategyName);
    }

    /// <summary>
    /// Unsubscribe from specific strategy updates
    /// </summary>
    public async Task UnsubscribeFromStrategy(string strategyName)
    {
        var groupName = $"Strategy_{strategyName}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        
        _logger.LogDebug("Client {ConnectionId} unsubscribed from strategy {StrategyName}", 
            Context.ConnectionId, strategyName);
    }

    /// <summary>
    /// Get current portfolio statistics
    /// </summary>
    public async Task<object> GetPortfolioStatistics()
    {
        try
        {
            if (_strategyManager != null)
            {
                var stats = await _strategyManager.GetPortfolioStatistics();
                return new
                {
                    Success = true,
                    Data = stats,
                    Timestamp = DateTime.UtcNow
                };
            }
            
            return new
            {
                Success = false,
                Error = "Strategy manager not available",
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting portfolio statistics");
            return new
            {
                Success = false,
                Error = ex.Message,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Get strategy-specific statistics
    /// </summary>
    public async Task<object> GetStrategyStatistics(string strategyName)
    {
        try
        {
            if (_strategyManager != null)
            {
                var stats = _strategyManager.GetStrategyStatistics(strategyName);
                return new
                {
                    Success = true,
                    Data = stats,
                    StrategyName = strategyName,
                    Timestamp = DateTime.UtcNow
                };
            }
            
            return new
            {
                Success = false,
                Error = "Strategy manager not available",
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting strategy statistics for {StrategyName}", strategyName);
            return new
            {
                Success = false,
                Error = ex.Message,
                StrategyName = strategyName,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Enable or disable a specific strategy
    /// </summary>
    public async Task SetStrategyEnabled(string strategyName, bool enabled)
    {
        try
        {
            if (_strategyManager != null)
            {
                await _strategyManager.SetStrategyEnabled(strategyName, enabled);
                
                // Notify all clients about the strategy status change
                await Clients.Group("TradingUpdates").SendAsync("StrategyStatusChanged", new
                {
                    StrategyName = strategyName,
                    Enabled = enabled,
                    Timestamp = DateTime.UtcNow
                });
                
                _logger.LogInformation("Strategy {StrategyName} {Status} by client {ConnectionId}", 
                    strategyName, enabled ? "enabled" : "disabled", Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting strategy {StrategyName} enabled status to {Enabled}", 
                strategyName, enabled);
            
            // Send error back to calling client
            await Clients.Caller.SendAsync("Error", new
            {
                Message = $"Failed to {(enabled ? "enable" : "disable")} strategy {strategyName}",
                Details = ex.Message,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Emergency stop all strategies
    /// </summary>
    public async Task EmergencyStop(string reason)
    {
        try
        {
            if (_strategyManager != null)
            {
                await _strategyManager.EmergencyStopAsync(reason);
                
                // Notify all clients about emergency stop
                await Clients.Group("TradingUpdates").SendAsync("EmergencyStop", new
                {
                    Reason = reason,
                    Timestamp = DateTime.UtcNow,
                    InitiatedBy = Context.ConnectionId
                });
                
                _logger.LogWarning("Emergency stop initiated by client {ConnectionId}. Reason: {Reason}", 
                    Context.ConnectionId, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during emergency stop");
            
            await Clients.Caller.SendAsync("Error", new
            {
                Message = "Failed to execute emergency stop",
                Details = ex.Message,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Send initial data to newly connected client
    /// </summary>
    private async Task SendInitialData()
    {
        try
        {
            if (_strategyManager != null)
            {
                // Send current portfolio statistics
                var portfolioStats = await _strategyManager.GetPortfolioStatistics();
                await Clients.Caller.SendAsync("PortfolioStatistics", portfolioStats);
                
                // Send individual strategy statistics
                var strategies = new[] { "TriangularArbitrage", "OptimalMarketMaking", "MLMomentum" };
                
                foreach (var strategyName in strategies)
                {
                    var strategyStats = _strategyManager.GetStrategyStatistics(strategyName);
                    if (strategyStats != null)
                    {
                        await Clients.Caller.SendAsync("StrategyStatistics", new
                        {
                            StrategyName = strategyName,
                            Statistics = strategyStats
                        });
                    }
                }
            }
            
            // Send system status
            await Clients.Caller.SendAsync("SystemStatus", new
            {
                Status = "Connected",
                Timestamp = DateTime.UtcNow,
                Version = "1.0.0"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending initial data to client {ConnectionId}", Context.ConnectionId);
        }
    }
}

/// <summary>
/// Extension methods for TradingHub to broadcast updates from other services
/// </summary>
public static class TradingHubExtensions
{
    /// <summary>
    /// Broadcast market data update to all connected clients
    /// </summary>
    public static async Task BroadcastMarketDataUpdate(this IHubContext<TradingHub> hubContext, 
        MarketDataEvent marketData)
    {
        var symbol = GetSymbolName(marketData.SymbolId);
        var price = PriceTicksToDecimal(marketData.PriceTicks);
        var volume = QuantityTicksToDecimal(marketData.Quantity);
        var side = marketData.Side.ToString();
        var timestamp = marketData.Timestamp;
        
        // Debug logging (commented to reduce console noise)
        // Console.WriteLine($"Broadcasting: {symbol} - Price: {price}, Volume: {volume}, Side: {side}, Time: {timestamp:HH:mm:ss}");
        
        await hubContext.Clients.Group("TradingUpdates").SendAsync("MarketDataUpdate", new
        {
            Symbol = symbol,
            Price = price,
            Volume = volume,
            Side = side,
            Timestamp = timestamp
        });
    }

    /// <summary>
    /// Broadcast portfolio statistics update
    /// </summary>
    public static async Task BroadcastPortfolioUpdate(this IHubContext<TradingHub> hubContext, 
        object portfolioStats)
    {
        await hubContext.Clients.Group("TradingUpdates").SendAsync("PortfolioStatistics", portfolioStats);
    }

    /// <summary>
    /// Broadcast strategy-specific update
    /// </summary>
    public static async Task BroadcastStrategyUpdate(this IHubContext<TradingHub> hubContext, 
        string strategyName, object strategyStats)
    {
        await hubContext.Clients.Group($"Strategy_{strategyName}")
            .SendAsync("StrategyStatistics", new
            {
                StrategyName = strategyName,
                Statistics = strategyStats,
                Timestamp = DateTime.UtcNow
            });
    }

    /// <summary>
    /// Broadcast system alert to all clients
    /// </summary>
    public static async Task BroadcastSystemAlert(this IHubContext<TradingHub> hubContext, 
        string level, string message, string? details = null)
    {
        await hubContext.Clients.Group("TradingUpdates").SendAsync("SystemAlert", new
        {
            Level = level,
            Message = message,
            Details = details,
            Timestamp = DateTime.UtcNow
        });
    }

    private static string GetSymbolName(int symbolId)
    {
        return symbolId switch
        {
            1 => "BTCUSDT",
            2 => "ETHUSDT", 
            3 => "ADAUSDT",
            _ => $"SYMBOL_{symbolId}"
        };
    }

    private static decimal PriceTicksToDecimal(long priceTicks)
    {
        // Convert ticks to decimal - for crypto prices, typically 4-8 decimal places
        // Based on the logs showing prices like 111,000+, this seems to be already in correct format
        return priceTicks / 10000m; // Assuming 4 decimal places for price precision
    }

    private static decimal QuantityTicksToDecimal(long quantityTicks)
    {
        // Convert quantity ticks to decimal - crypto quantities often have 8 decimal places
        // Based on logs showing large numbers, this needs proper scaling
        return quantityTicks / 100000000m; // Assuming 8 decimal places for crypto quantities
    }
}
