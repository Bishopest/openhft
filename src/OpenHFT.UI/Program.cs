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
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using Disruptor.Dsl;
using OpenHFT.Processing;

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
    services.AddSingleton<IInstrumentRepository, InstrumentRepository>();
    services.AddSingleton<MarketDataDistributor>();
    services.AddSingleton<IOrderUpdateHandler, OrderUpdateDistributor>();
    services.AddSingleton(provider =>
    {
        var disruptor = new Disruptor<MarketDataEventWrapper>(() => new MarketDataEventWrapper(), 1024);
        disruptor.HandleEventsWith(provider.GetRequiredService<MarketDataDistributor>());
        return disruptor;
    });
    services.AddSingleton(provider =>
    {
        var disruptor = new Disruptor<OrderStatusReportWrapper>(() => new OrderStatusReportWrapper(), 1024);
        disruptor.HandleEventsWith(provider.GetRequiredService<IOrderUpdateHandler>());
        return disruptor;
    });

    // Feed components
    services.AddSingleton<IFeedAdapter>(provider => new BinanceAdapter(
                                    provider.GetRequiredService<ILogger<BinanceAdapter>>(), ProductType.PerpetualFuture,
                                    provider.GetRequiredService<IInstrumentRepository>(), ExecutionMode.Testnet
                                ));
    services.AddSingleton<IFeedHandler, FeedHandler>();
    services.AddSingleton<IFeedAdapterRegistry, FeedAdapterRegistry>();
    // Main engine
    services.AddSingleton<HftEngine>();
    services.AddHostedService(provider => provider.GetRequiredService<HftEngine>());

    Log.Information("HFT services registered successfully");
}
