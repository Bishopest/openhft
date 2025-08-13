using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Prometheus;
using OpenHFT.UI.Services;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Adapters;
using OpenHFT.Strategy.Interfaces;
using OpenHFT.Strategy.Strategies;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/openhft-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS for development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Add SPA services
builder.Services.AddSpaStaticFiles(configuration =>
{
    configuration.RootPath = "ClientApp/build";
});

// Register HFT components
RegisterHftServices(builder.Services, builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors();

// Prometheus metrics endpoint
app.UseMetricServer();
app.UseHttpMetrics();

app.MapControllers();

// Serve static files for React UI
app.UseStaticFiles();
app.UseSpaStaticFiles();

// SPA fallback
app.UseSpa(spa =>
{
    spa.Options.SourcePath = "ClientApp";
    
    if (app.Environment.IsDevelopment())
    {
        spa.UseProxyToSpaDevelopmentServer("http://localhost:3000");
    }
});

try
{
    Log.Information("Starting OpenHFT-Lab application");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static void RegisterHftServices(IServiceCollection services, IConfiguration configuration)
{
    // Feed components
    services.AddSingleton<IFeedAdapter, BinanceAdapter>();
    services.AddSingleton<IFeedHandler, FeedHandler>();

    // Strategy components
    services.AddSingleton<IStrategyEngine, StrategyEngine>();
    services.AddSingleton<IStrategy, MarketMakingStrategy>();

    // Main engine
    services.AddSingleton<HftEngine>();
    services.AddHostedService<HftEngine>();

    // Configure strategies
    var strategyConfig = new OpenHFT.Strategy.Interfaces.StrategyConfiguration
    {
        Name = "MarketMaking",
        Symbols = configuration.GetSection("Engine:Symbols").Get<List<string>>() ?? new List<string> { "BTCUSDT" },
        Enabled = true,
        Parameters = new Dictionary<string, object>
        {
            ["BaseSpreadTicks"] = 2,
            ["MaxPosition"] = 1000m,
            ["QuoteSizeTicks"] = 100m,
            ["MaxOrderSize"] = 500m
        }
    };

    services.AddSingleton(strategyConfig);

    Log.Information("HFT services registered successfully");
}

// Placeholder implementations for missing classes
public class FeedHandler : IFeedHandler
{
    private readonly ILogger<FeedHandler> _logger;
    private readonly List<IFeedAdapter> _adapters = new();
    private OpenHFT.Core.Collections.LockFreeRingBuffer<OpenHFT.Core.Models.MarketDataEvent>? _marketDataQueue;

    public IReadOnlyList<IFeedAdapter> Adapters => _adapters.AsReadOnly();
    public OpenHFT.Feed.Interfaces.FeedHandlerStatistics Statistics { get; } = new();

    public event EventHandler<OpenHFT.Core.Models.MarketDataEvent>? MarketDataReceived;
    public event EventHandler<OpenHFT.Feed.Interfaces.GapDetectedEventArgs>? GapDetected;

    public FeedHandler(ILogger<FeedHandler> logger, IFeedAdapter adapter)
    {
        _logger = logger;
        _adapters.Add(adapter);
        
        // Subscribe to adapter events
        adapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;
        adapter.Error += OnAdapterError;
        adapter.MarketDataReceived += OnMarketDataReceived;
    }

    public void Initialize(OpenHFT.Core.Collections.LockFreeRingBuffer<OpenHFT.Core.Models.MarketDataEvent> marketDataQueue)
    {
        _marketDataQueue = marketDataQueue;
        Statistics.StartTime = DateTimeOffset.UtcNow;
        _logger.LogInformation("Feed handler initialized with {AdapterCount} adapters", _adapters.Count);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var adapter in _adapters)
        {
            await adapter.ConnectAsync(cancellationToken);
            await adapter.SubscribeAsync(new[] { "BTCUSDT", "ETHUSDT" }, cancellationToken);
            await adapter.StartAsync(cancellationToken);
        }
        
        _logger.LogInformation("Feed handler started");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var adapter in _adapters)
        {
            await adapter.StopAsync(cancellationToken);
        }
        
        _logger.LogInformation("Feed handler stopped");
    }

    public void AddAdapter(IFeedAdapter adapter)
    {
        _adapters.Add(adapter);
        adapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;
        adapter.Error += OnAdapterError;
        adapter.MarketDataReceived += OnMarketDataReceived;
    }

    public void RemoveAdapter(IFeedAdapter adapter)
    {
        _adapters.Remove(adapter);
        adapter.ConnectionStateChanged -= OnAdapterConnectionStateChanged;
        adapter.Error -= OnAdapterError;
        adapter.MarketDataReceived -= OnMarketDataReceived;
    }

    private void OnAdapterConnectionStateChanged(object? sender, OpenHFT.Feed.Interfaces.ConnectionStateChangedEventArgs e)
    {
        _logger.LogInformation("Adapter connection state changed: {IsConnected} - {Reason}", e.IsConnected, e.Reason);
    }

    private void OnAdapterError(object? sender, OpenHFT.Feed.Interfaces.FeedErrorEventArgs e)
    {
        _logger.LogError(e.Exception, "Adapter error: {Context}", e.Context);
    }

    private void OnMarketDataReceived(object? sender, OpenHFT.Core.Models.MarketDataEvent marketDataEvent)
    {
        // Forward market data to the queue for processing
        if (_marketDataQueue != null)
        {
            if (!_marketDataQueue.TryWrite(marketDataEvent))
            {
                _logger.LogWarning("Market data queue is full, dropping event for symbol {SymbolId}", marketDataEvent.SymbolId);
            }
        }
    }

    public void Dispose()
    {
        foreach (var adapter in _adapters)
        {
            adapter.Dispose();
        }
    }
}

public class StrategyEngine : IStrategyEngine
{
    private readonly ILogger<StrategyEngine> _logger;
    private readonly List<IStrategy> _strategies = new();

    public event EventHandler<OpenHFT.Strategy.Interfaces.StrategyOrderEventArgs>? OrderGenerated;

    public StrategyEngine(ILogger<StrategyEngine> logger)
    {
        _logger = logger;
    }

    public void RegisterStrategy(IStrategy strategy)
    {
        _strategies.Add(strategy);
        strategy.OrderGenerated += OnStrategyOrderGenerated;
        _logger.LogInformation("Registered strategy: {StrategyName}", strategy.Name);
    }

    public void UnregisterStrategy(IStrategy strategy)
    {
        _strategies.Remove(strategy);
        strategy.OrderGenerated -= OnStrategyOrderGenerated;
        _logger.LogInformation("Unregistered strategy: {StrategyName}", strategy.Name);
    }

    public IReadOnlyList<IStrategy> GetStrategies() => _strategies.AsReadOnly();

    public IEnumerable<OpenHFT.Core.Models.OrderIntent> ProcessMarketData(OpenHFT.Core.Models.MarketDataEvent marketDataEvent, OpenHFT.Book.Core.OrderBook orderBook)
    {
        var orders = new List<OpenHFT.Core.Models.OrderIntent>();
        
        foreach (var strategy in _strategies.Where(s => s.IsEnabled))
        {
            try
            {
                var strategyOrders = strategy.OnMarketData(marketDataEvent, orderBook);
                orders.AddRange(strategyOrders);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in strategy {StrategyName} processing market data", strategy.Name);
            }
        }
        
        return orders;
    }

    public void ProcessOrderAck(OpenHFT.Core.Models.OrderAck orderAck)
    {
        foreach (var strategy in _strategies)
        {
            try
            {
                strategy.OnOrderAck(orderAck);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in strategy {StrategyName} processing order ack", strategy.Name);
            }
        }
    }

    public void ProcessFill(OpenHFT.Core.Models.FillEvent fillEvent)
    {
        foreach (var strategy in _strategies)
        {
            try
            {
                strategy.OnFill(fillEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in strategy {StrategyName} processing fill", strategy.Name);
            }
        }
    }

    public IEnumerable<OpenHFT.Core.Models.OrderIntent> ProcessTimer(DateTimeOffset timestamp)
    {
        var orders = new List<OpenHFT.Core.Models.OrderIntent>();
        
        foreach (var strategy in _strategies.Where(s => s.IsEnabled))
        {
            try
            {
                var strategyOrders = strategy.OnTimer(timestamp);
                orders.AddRange(strategyOrders);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in strategy {StrategyName} timer processing", strategy.Name);
            }
        }
        
        return orders;
    }

    public async Task StartAsync()
    {
        foreach (var strategy in _strategies)
        {
            await strategy.StartAsync();
        }
        
        _logger.LogInformation("Strategy engine started with {StrategyCount} strategies", _strategies.Count);
    }

    public async Task StopAsync()
    {
        foreach (var strategy in _strategies)
        {
            await strategy.StopAsync();
        }
        
        _logger.LogInformation("Strategy engine stopped");
    }

    public OpenHFT.Strategy.Interfaces.StrategyEngineStatistics GetStatistics()
    {
        return new OpenHFT.Strategy.Interfaces.StrategyEngineStatistics
        {
            ActiveStrategies = _strategies.Count(s => s.IsEnabled),
            StartTime = DateTimeOffset.UtcNow.AddHours(-1) // Placeholder
        };
    }

    private void OnStrategyOrderGenerated(object? sender, OpenHFT.Core.Models.OrderIntent orderIntent)
    {
        var strategy = sender as IStrategy;
        if (strategy != null)
        {
            OrderGenerated?.Invoke(this, new OpenHFT.Strategy.Interfaces.StrategyOrderEventArgs(strategy.Name, orderIntent));
        }
    }

    public void Dispose()
    {
        foreach (var strategy in _strategies)
        {
            strategy.Dispose();
        }
    }
}
