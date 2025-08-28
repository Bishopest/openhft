using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenHFT.Strategy.Advanced;
using OpenHFT.Strategy.Advanced.Arbitrage;
using OpenHFT.Strategy.Advanced.MarketMaking;
using OpenHFT.Strategy.Advanced.Momentum;
using OpenHFT.Core.Models;
using OpenHFT.Book.Models;
using OpenHFT.Book.Core;

namespace OpenHFT.Strategy.Advanced;

/// <summary>
/// Advanced HFT Strategy Demonstration Program
/// Showcases triangular arbitrage, market making, and ML momentum strategies
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("🚀 OpenHFT Advanced Strategy Demonstration");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        // Build service collection with dependency injection
        var services = new ServiceCollection();
        ConfigureServices(services);
        
        var serviceProvider = services.BuildServiceProvider();
        
        try
        {
            await RunAdvancedStrategyDemo(serviceProvider);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error during demonstration: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        finally
        {
            serviceProvider.Dispose();
        }
        
        Console.WriteLine();
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging configuration
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Advanced strategy configuration
        var config = new AdvancedStrategyConfig
        {
            EnableArbitrage = true,
            EnableMarketMaking = true,
            EnableMomentum = true,
            ArbitrageAllocation = 0.3m,
            MarketMakingAllocation = 0.5m,
            MomentumAllocation = 0.2m,
            MaxArbitragePosition = 10m,
            MaxMarketMakingPosition = 50m,
            MaxMomentumPosition = 20m,
            ArbitrageRiskLimit = 0.05m,
            MarketMakingRiskLimit = 0.03m
        };

        services.AddSingleton(config);

        // Register strategies
        services.AddSingleton<TriangularArbitrageStrategy>();
        services.AddSingleton<OptimalMarketMakingStrategy>();
        services.AddSingleton<MLMomentumStrategy>();

        // Register managers
        services.AddSingleton<IAdvancedStrategyManager, AdvancedStrategyManager>();
        services.AddSingleton<RiskManager>();
        services.AddSingleton<PerformanceAnalyzer>();
    }

    private static async Task RunAdvancedStrategyDemo(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        var strategyManager = services.GetRequiredService<IAdvancedStrategyManager>();

        logger.LogInformation("🎯 Starting Advanced Strategy Demonstration");
        
        // Create sample market data
        var marketEvents = GenerateRealisticMarketData();
        var orderBooks = CreateSampleOrderBooks();

        Console.WriteLine("📊 Processing Market Data Events:");
        Console.WriteLine("=================================");

        int eventCounter = 0;
        foreach (var marketEvent in marketEvents)
        {
            eventCounter++;
            var orderBook = orderBooks[marketEvent.SymbolId % orderBooks.Count];
            
            // Update order book with market event (simulation)
            UpdateOrderBookWithEvent(orderBook, marketEvent);
            
            Console.WriteLine($"Event {eventCounter:D2}: {GetEventDescription(marketEvent)}");
            
            try
            {
                // Process through strategy manager
                var orders = await strategyManager.ProcessMarketDataAsync(marketEvent, orderBook);
                
                if (orders.Any())
                {
                    Console.WriteLine($"         📝 Generated {orders.Count()} orders:");
                    foreach (var order in orders.Take(3)) // Show first 3 orders
                    {
                        Console.WriteLine($"            • {order.Side} {PriceTicksToDecimal(order.Quantity):F4} @ {PriceTicksToDecimal(order.PriceTicks):F2} ({order.Type})");
                    }
                    if (orders.Count() > 3)
                    {
                        Console.WriteLine($"            ... and {orders.Count() - 3} more orders");
                    }
                }
                else
                {
                    Console.WriteLine("         📝 No orders generated");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"         ❌ Error processing event: {ex.Message}");
            }
            
            Console.WriteLine();
            
            // Small delay for visualization
            await Task.Delay(200);
        }

        // Display final performance summary
        await DisplayPerformanceSummary(strategyManager);
    }

    private static List<MarketDataEvent> GenerateRealisticMarketData()
    {
        var events = new List<MarketDataEvent>();
        var random = new Random(42); // Seed for reproducible results
        var baseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        
        // Simulate multiple trading pairs
        var tradingPairs = new[]
        {
            (symbolId: 1, name: "BTC/USDT", basePrice: 43000m),
            (symbolId: 2, name: "ETH/USDT", basePrice: 2800m),
            (symbolId: 3, name: "BTC/ETH", basePrice: 15.36m)
        };

        for (int i = 0; i < 25; i++)
        {
            foreach (var pair in tradingPairs)
            {
                // Generate realistic price movements
                var volatility = pair.symbolId switch
                {
                    1 => 0.02m, // BTC 2% volatility
                    2 => 0.03m, // ETH 3% volatility
                    3 => 0.01m, // BTC/ETH 1% volatility
                    _ => 0.02m
                };
                
                var priceChange = (decimal)(random.NextDouble() - 0.5) * 2 * volatility;
                var currentPrice = pair.basePrice * (1 + priceChange);
                
                var quantity = pair.symbolId switch
                {
                    1 => 0.1m + (decimal)random.NextDouble() * 2.0m, // 0.1-2.1 BTC
                    2 => 1.0m + (decimal)random.NextDouble() * 10.0m, // 1-11 ETH
                    3 => 5.0m + (decimal)random.NextDouble() * 20.0m, // 5-25 BTC in ETH terms
                    _ => 1.0m
                };

                var eventKind = random.NextDouble() switch
                {
                    < 0.7 => EventKind.Trade,
                    < 0.9 => EventKind.Update,
                    _ => EventKind.Add
                };

                var side = random.NextDouble() < 0.5 ? Side.Buy : Side.Sell;

                var marketEvent = new MarketDataEvent(
                    sequence: (long)(i * tradingPairs.Length + pair.symbolId),
                    timestamp: baseTimestamp + (i * 1000) + (pair.symbolId * 100),
                    side: side,
                    priceTicks: DecimalToPriceTicks(currentPrice),
                    quantity: DecimalToQuantityTicks(quantity),
                    kind: eventKind,
                    symbolId: pair.symbolId
                );

                events.Add(marketEvent);
            }
        }

        return events.OrderBy(e => e.Timestamp).ToList();
    }

    private static List<OrderBook> CreateSampleOrderBooks()
    {
        return new List<OrderBook>
        {
            new OrderBook("BTCUSDT"), // Symbol ID 1
            new OrderBook("ETHUSDT"), // Symbol ID 2
            new OrderBook("BTCETH")   // Symbol ID 3
        };
    }

    private static void UpdateOrderBookWithEvent(OrderBook orderBook, MarketDataEvent marketEvent)
    {
        // In a real system, this would be handled by the market data feed
        // For demo purposes, we'll just apply the event to the order book
        try
        {
            orderBook.ApplyEvent(marketEvent);
        }
        catch (Exception)
        {
            // Ignore errors in demo - order book might not have all required data
        }
    }

    private static string GetEventDescription(MarketDataEvent marketEvent)
    {
        var symbolName = marketEvent.SymbolId switch
        {
            1 => "BTC/USDT",
            2 => "ETH/USDT", 
            3 => "BTC/ETH",
            _ => $"Symbol{marketEvent.SymbolId}"
        };
        
        return $"{marketEvent.Kind} | {symbolName} | ${PriceTicksToDecimal(marketEvent.PriceTicks):F2} | Vol: {PriceTicksToDecimal(marketEvent.Quantity):F4}";
    }

    private static async Task DisplayPerformanceSummary(IAdvancedStrategyManager strategyManager)
    {
        Console.WriteLine("📈 Strategy Performance Summary");
        Console.WriteLine("==============================");
        
        try
        {
            var stats = await strategyManager.GetPortfolioStatistics();
            
            Console.WriteLine($"Total Strategies: {stats.TotalStrategies}");
            Console.WriteLine($"Active Strategies: {stats.ActiveStrategies}");
            Console.WriteLine($"Total PnL: ${stats.TotalPnL:F2}");
            Console.WriteLine($"Average Sharpe: {stats.AverageSharpe:F3}");
            Console.WriteLine($"Success Rate: {stats.OverallSuccessRate:P2}");
            Console.WriteLine($"Max Drawdown: {stats.MaxDrawdown:P2}");
            Console.WriteLine($"Active Positions: {stats.TotalActivePositions}");
            Console.WriteLine($"Order Execution Rate: {stats.OrderExecutionRate:P2}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unable to retrieve performance statistics: {ex.Message}");
        }
        
        Console.WriteLine();
        Console.WriteLine("✅ Advanced Strategy Demonstration Complete!");
        Console.WriteLine();
        Console.WriteLine("🔥 Features Demonstrated:");
        Console.WriteLine("   • Triangular Arbitrage Detection");
        Console.WriteLine("   • Optimal Market Making with Inventory Management");
        Console.WriteLine("   • ML-Powered Momentum Strategy");
        Console.WriteLine("   • Real-time Risk Management");
        Console.WriteLine("   • Multi-symbol Order Book Processing");
        Console.WriteLine("   • Performance Analytics");
    }

    // Helper methods for price/quantity conversion
    private static long DecimalToPriceTicks(decimal price) => (long)(price * 100);
    private static long DecimalToQuantityTicks(decimal quantity) => (long)(quantity * 100_000_000);
    private static decimal PriceTicksToDecimal(long ticks) => ticks * 0.01m;
}
