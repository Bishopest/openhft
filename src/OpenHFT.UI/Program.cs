using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Prometheus;
using MudBlazor.Services;
using OpenHFT.Feed;
using OpenHFT.UI.Services;
using OpenHFT.UI.Hubs;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Adapters;
using OpenHFT.Strategy.Interfaces;
using OpenHFT.Strategy.Strategies;
using OpenHFT.Strategy.Advanced;
using OpenHFT.Strategy.Advanced.Arbitrage;
using OpenHFT.Strategy.Advanced.MarketMaking;
using OpenHFT.Strategy.Advanced.Momentum;

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

// Add Blazor services
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Add SignalR services
builder.Services.AddSignalR();

// Add MudBlazor services
builder.Services.AddMudServices();

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
app.MapRazorPages();
app.MapBlazorHub();
app.MapHub<OpenHFT.UI.Hubs.TradingHub>("/tradinghub");
app.MapFallbackToPage("/_Host");

// Serve static files
app.UseStaticFiles();

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

    // Advanced Strategy Manager configuration
    services.AddSingleton<AdvancedStrategyConfig>(provider =>
    {
        return new AdvancedStrategyConfig
        {
            EnableArbitrage = true,
            ArbitrageAllocation = 10000m,
            MaxArbitragePosition = 1000m,
            ArbitrageRiskLimit = 0.02m,

            EnableMarketMaking = true,
            MarketMakingAllocation = 15000m,
            MaxMarketMakingPosition = 2000m,
            MarketMakingRiskLimit = 0.03m,

            EnableMomentum = true,
            MomentumAllocation = 20000m,
            MaxMomentumPosition = 1500m,
            MomentumRiskLimit = 0.025m,

            RiskConfig = new RiskManagementConfig
            {
                MaxDrawdown = 0.10m,
                MaxPositionPerSymbol = 100m,
                MaxOrderSize = 50m,
                MinOrderSize = 0.01m,
                MaxConcentrationPerSymbol = 0.25m
            }
        };
    });

    // Register individual advanced strategies
    services.AddSingleton<TriangularArbitrageStrategy>();
    services.AddSingleton<OptimalMarketMakingStrategy>();
    services.AddSingleton<MLMomentumStrategy>();

    services.AddSingleton<IAdvancedStrategyManager, AdvancedStrategyManager>();

    // Register AdvancedStrategyManager as hosted service to auto-initialize strategies
    services.AddHostedService<AdvancedStrategyManager>(provider =>
        (AdvancedStrategyManager)provider.GetRequiredService<IAdvancedStrategyManager>());

    // Main engine
    services.AddSingleton<HftEngine>();
    services.AddHostedService(provider => provider.GetRequiredService<HftEngine>());

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
