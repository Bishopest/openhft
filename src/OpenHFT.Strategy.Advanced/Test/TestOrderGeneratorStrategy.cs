using OpenHFT.Core.Models;
using OpenHFT.Book.Models;
using OpenHFT.Book.Core;
using OpenHFT.Strategy.Interfaces;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;

namespace OpenHFT.Strategy.Advanced.Test;

/// <summary>
/// Simple test strategy that generates orders periodically to verify the order processing pipeline
/// </summary>
public class TestOrderGeneratorStrategy : IAdvancedStrategy
{
    private readonly ILogger<TestOrderGeneratorStrategy> _logger;
    private long _processCount = 0;
    private long _orderCount = 0;

    public TestOrderGeneratorStrategy(ILogger<TestOrderGeneratorStrategy> logger)
    {
        _logger = logger;
    }

    public string Name => "TestOrderGenerator";
    public AdvancedStrategyState State { get; private set; } = AdvancedStrategyState.Running;

    public async Task<List<OrderIntent>> ProcessMarketData(MarketDataEvent marketData, OrderBook orderBook)
    {
        Interlocked.Increment(ref _processCount);

        // Log every 50 calls to show activity
        if (_processCount % 50 == 0)
        {
            _logger.LogInformation("TestOrderGenerator processed {Count} market events", _processCount);
        }

        var orders = new List<OrderIntent>();

        // Generate one test order every 100 market events
        if (_processCount % 100 == 0)
        {
            var bestPrice = GetBestPrice(orderBook);
            if (bestPrice != null)
            {
                Interlocked.Increment(ref _orderCount);

                var order = new OrderIntent(
                    clientOrderId: TimestampUtils.GetTimestampMicros(),
                    type: OrderTypeEnum.Limit,
                    side: Side.Buy,
                    priceTicks: DecimalToPriceTicks(bestPrice.Value.bid * 0.99m), // 1% below bid
                    quantity: DecimalToQuantityTicks(0.001m), // Very small quantity
                    timestampIn: TimestampUtils.GetTimestampMicros(),
                    symbolId: marketData.InstrumentId
                );

                orders.Add(order);

                _logger.LogInformation(
                    "TestOrderGenerator created order #{OrderCount}: {Side} {Quantity} at {Price} for symbol {SymbolId}",
                    _orderCount, order.Side, PriceTicksToDecimal(order.Quantity),
                    PriceTicksToDecimal(order.PriceTicks), marketData.InstrumentId);
            }
        }

        return orders;
    }

    private (decimal bid, decimal ask)? GetBestPrice(OrderBook orderBook)
    {
        var bid = orderBook.GetBestBid().priceTicks;
        var ask = orderBook.GetBestAsk().priceTicks;

        if (bid == null || ask == null)
            return null;

        return (PriceTicksToDecimal(bid), PriceTicksToDecimal(ask));
    }

    private decimal PriceTicksToDecimal(long priceTicks)
    {
        return priceTicks / 100_000_000m; // Assuming 8 decimal places
    }

    private long DecimalToPriceTicks(decimal price)
    {
        return (long)(price * 100_000_000m); // Assuming 8 decimal places
    }

    private long DecimalToQuantityTicks(decimal quantity)
    {
        return (long)(quantity * 100_000_000m); // Assuming 8 decimal places
    }

    public Task StartAsync()
    {
        State = AdvancedStrategyState.Running;
        _logger.LogInformation("TestOrderGenerator strategy started");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        State = AdvancedStrategyState.Stopped;
        _logger.LogInformation("TestOrderGenerator strategy stopped. Processed {ProcessCount} events, generated {OrderCount} orders",
            _processCount, _orderCount);
        return Task.CompletedTask;
    }

    public StrategyStatistics GetStatistics()
    {
        return new StrategyStatistics
        {
            StrategyName = Name,
            TotalTrades = _orderCount,
            TotalPnL = 0,
            Sharpe = 0,
            MaxDrawdown = 0,
            AverageHoldTime = TimeSpan.Zero,
            LastUpdate = DateTime.UtcNow
        };
    }
}
