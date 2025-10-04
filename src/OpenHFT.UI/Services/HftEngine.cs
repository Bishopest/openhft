using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using OpenHFT.Core.Models;
using OpenHFT.Core.Collections;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Book.Core;
using OpenHFT.Strategy.Interfaces;
using OpenHFT.Strategy.Advanced;
using OpenHFT.UI.Hubs;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Instruments;

namespace OpenHFT.UI.Services;

/// <summary>
/// Main HFT Engine that orchestrates all components
/// Coordinates market data flow from feeds through strategies to execution
/// </summary>
public class HftEngine : BackgroundService
{
    private readonly ILogger<HftEngine> _logger;
    private readonly IConfiguration _configuration;
    private readonly IFeedHandler _feedHandler;
    private readonly IInstrumentRepository _repository;
    private readonly IStrategyEngine _strategyEngine;
    private readonly IAdvancedStrategyManager _advancedStrategyManager;
    private readonly IHubContext<TradingHub> _hubContext;

    // Core data structures
    private readonly LockFreeRingBuffer<MarketDataEvent> _marketDataQueue;
    private readonly Dictionary<string, OrderBook> _orderBooks = new();

    // Engine state
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private long _eventsProcessed;
    private long _ordersGenerated;
    private DateTimeOffset _startTime;
    private long _broadcastCounter;

    public HftEngine(
        ILogger<HftEngine> logger,
        IConfiguration configuration,
        IFeedHandler feedHandler,
        IStrategyEngine strategyEngine,
        IAdvancedStrategyManager advancedStrategyManager,
        IHubContext<TradingHub> hubContext,
        IInstrumentRepository repository)
    {
        _logger = logger;
        _configuration = configuration;
        _feedHandler = feedHandler;
        _repository = repository;
        _strategyEngine = strategyEngine;
        _advancedStrategyManager = advancedStrategyManager;
        _hubContext = hubContext;

        // Initialize market data queue with high capacity for bursts
        var queueSize = configuration.GetValue<int>("Engine:MarketDataQueueSize", 65536);
        _marketDataQueue = new LockFreeRingBuffer<MarketDataEvent>(queueSize);

        // Wire up event handlers
        _strategyEngine.OrderGenerated += OnStrategyOrderGenerated;
    }

    public bool IsRunning => _isRunning;
    public bool IsPaused => _isPaused;
    public long EventsProcessed => _eventsProcessed;
    public long OrdersGenerated => _ordersGenerated;
    public TimeSpan Uptime => DateTimeOffset.UtcNow - _startTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting HFT Engine...");
        _startTime = DateTimeOffset.UtcNow;

        try
        {
            // Initialize components
            await InitializeAsync(stoppingToken);

            // Start market data processing loop
            await RunMainLoop(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("HFT Engine stopped by cancellation request");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in HFT Engine");
            throw;
        }
        finally
        {
            await ShutdownAsync();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing HFT Engine components...");

        // Initialize order books for configured symbols
        var symbols = _configuration.GetSection("Engine:Symbols").Get<string[]>() ?? Array.Empty<string>();
        foreach (var symbol in symbols)
        {
            var instrument = _repository.FindBySymbol(symbol, ProductType.PerpetualFuture, ExchangeEnum.BINANCE);
            if (instrument == null)
            {
                continue;
            }
            _orderBooks[symbol] = new OrderBook(instrument, null);
            _logger.LogInformation("Created order book for {Symbol}", symbol);
        }

        // Initialize feed handler
        // _feedHandler.Initialize(_marketDataQueue);
        try
        {
            await _feedHandler.StartAsync(cancellationToken);
            _logger.LogInformation("Feed handler started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start feed handler - running in offline mode");
        }

        // Initialize strategy engine
        await _strategyEngine.StartAsync();

        _isRunning = true;
        _logger.LogInformation("HFT Engine initialization complete. Processing {SymbolCount} symbols.", symbols.Length);
    }

    private async Task RunMainLoop(CancellationToken stoppingToken)
    {
        var processingStats = new ProcessingStats();
        var lastStatsTime = DateTimeOffset.UtcNow;

        _logger.LogInformation("Starting main processing loop...");

        while (!stoppingToken.IsCancellationRequested && _isRunning)
        {
            try
            {
                var processedThisRound = 0;

                // Process market data events in batches for better throughput
                while (_marketDataQueue.TryRead(out var marketDataEvent) && processedThisRound < 1000)
                {
                    if (_isPaused)
                    {
                        await Task.Delay(1, stoppingToken);
                        continue;
                    }

                    await ProcessMarketDataEvent(marketDataEvent);
                    processedThisRound++;
                    _eventsProcessed++;
                }

                // Periodic timer processing for strategies
                if (DateTimeOffset.UtcNow - processingStats.LastTimerRun > TimeSpan.FromMilliseconds(100))
                {
                    var timerOrders = _strategyEngine.ProcessTimer(DateTimeOffset.UtcNow);
                    foreach (var order in timerOrders)
                    {
                        await ProcessOrderIntent(order);
                    }

                    processingStats.LastTimerRun = DateTimeOffset.UtcNow;
                }

                // Log statistics periodically
                if (DateTimeOffset.UtcNow - lastStatsTime > TimeSpan.FromSeconds(10))
                {
                    LogStatistics();
                    lastStatsTime = DateTimeOffset.UtcNow;
                }

                // Small yield if no events processed to prevent CPU spinning
                if (processedThisRound == 0)
                {
                    await Task.Delay(1, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in main processing loop");

                // Brief pause before retrying to prevent tight error loops
                await Task.Delay(100, stoppingToken);
            }
        }

        _logger.LogInformation("Main processing loop ended");
    }

    private async Task ProcessMarketDataEvent(MarketDataEvent marketDataEvent)
    {
        var symbol = OpenHFT.Core.Utils.SymbolUtils.GetSymbol(marketDataEvent.InstrumentId);

        if (!_orderBooks.TryGetValue(symbol, out var orderBook))
        {
            _logger.LogWarning("No order book found for symbol {Symbol}", symbol);
            return;
        }

        // Apply event to order book
        if (!orderBook.ApplyEvent(marketDataEvent))
        {
            _logger.LogWarning("Failed to apply market data event for {Symbol}", symbol);
            return;
        }

        // Broadcast market data via SignalR (throttle to avoid overwhelming clients)
        _broadcastCounter++;
        if (_broadcastCounter % 50 == 0) // Only broadcast every 50th event to reduce noise
        {
            try
            {
                await _hubContext.BroadcastMarketDataUpdate(marketDataEvent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast market data update");
            }
        }

        // Process through basic strategies
        var basicOrderIntents = _strategyEngine.ProcessMarketData(marketDataEvent, orderBook);

        // Process through advanced strategies
        var advancedOrderIntents = await _advancedStrategyManager.ProcessMarketDataAsync(marketDataEvent, orderBook);

        // Combine all order intents
        var allOrderIntents = basicOrderIntents.Concat(advancedOrderIntents);

        // Send orders to execution
        foreach (var orderIntent in allOrderIntents)
        {
            await ProcessOrderIntent(orderIntent);
            Interlocked.Increment(ref _ordersGenerated);
        }
    }

    private async Task ProcessOrderIntent(OrderIntent orderIntent)
    {
        try
        {
            // TODO: Send to risk engine first
            // TODO: Send to order gateway
            // TODO: Track order state

            _ordersGenerated++;

            _logger.LogDebug("Order generated: {OrderIntent}", orderIntent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing order intent: {OrderIntent}", orderIntent);
        }
    }

    private void OnStrategyOrderGenerated(object? sender, StrategyOrderEventArgs e)
    {
        _logger.LogDebug("Strategy {StrategyName} generated order: {OrderIntent}",
            e.StrategyName, e.OrderIntent);
    }

    private void LogStatistics()
    {
        var rate = _eventsProcessed / Math.Max(1, Uptime.TotalSeconds);
        var queueDepth = _marketDataQueue.Count;

        _logger.LogInformation(
            "Engine Stats - Events: {EventsProcessed:N0} ({Rate:N0}/s), Orders: {OrdersGenerated:N0}, Queue: {QueueDepth}, Uptime: {Uptime}",
            _eventsProcessed, rate, _ordersGenerated, queueDepth, Uptime.ToString(@"hh\:mm\:ss"));

        // Log order book stats
        foreach (var (symbol, book) in _orderBooks)
        {
            var (bidPrice, bidQty) = book.GetBestBid();
            var (askPrice, askQty) = book.GetBestAsk();
            var spread = book.GetSpreadTicks();

            _logger.LogInformation(
                "{Symbol}: {BidPrice}@{BidQty} | {AskPrice}@{AskQty} (Spread: {Spread}, Updates: {Updates})",
                symbol,
                OpenHFT.Core.Utils.PriceUtils.FromTicks(bidPrice), bidQty,
                OpenHFT.Core.Utils.PriceUtils.FromTicks(askPrice), askQty,
                spread, book.UpdateCount);
        }
    }

    public async Task PauseAsync()
    {
        _isPaused = true;
        _logger.LogInformation("HFT Engine paused");
        await Task.CompletedTask;
    }

    public async Task ResumeAsync()
    {
        _isPaused = false;
        _logger.LogInformation("HFT Engine resumed");
        await Task.CompletedTask;
    }

    public IEnumerable<OrderBook> GetOrderBooks() => _orderBooks.Values;

    public OrderBook? GetOrderBook(string symbol) => _orderBooks.TryGetValue(symbol, out var book) ? book : null;

    private async Task ShutdownAsync()
    {
        _logger.LogInformation("Shutting down HFT Engine...");

        _isRunning = false;

        try
        {
            await _strategyEngine.StopAsync();
            await _feedHandler.StopAsync();

            _marketDataQueue.Dispose();

            _logger.LogInformation("HFT Engine shutdown complete. Total events processed: {EventsProcessed:N0}", _eventsProcessed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during engine shutdown");
        }
    }

    private class ProcessingStats
    {
        public DateTimeOffset LastTimerRun { get; set; } = DateTimeOffset.UtcNow;
    }
}
