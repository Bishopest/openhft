using OpenHFT.GUI.Components;
using MudBlazor.Services;
using OpenHFT.GUI.Services;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Instruments;
using OpenHFT.Feed.Adapters;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenHFT.Core.Configuration;
using Microsoft.Extensions.Options;
using OpenHFT.Feed;
using Serilog;
using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddScoped<IOrderBookManager, MockOrderBookManager>();
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};
builder.Services.AddSingleton(jsonOptions);
builder.Services.AddSingleton<IOrderCacheService, OrderCacheService>();
builder.Services.AddSingleton<IBookCacheService, BookCacheService>();
builder.Services.AddSingleton<IHedgingCacheService, HedgingCacheService>();
builder.Services.AddSingleton<IFxRateService, GuiFxRateManager>();
builder.Services.AddSingleton<IExchangeFeedManager, ExchangeFeedManager>();
builder.Services.AddSingleton<IOrderBookManager, OrderBookManager>();
builder.Services.AddSingleton<IOmsConnectorService, OmsConnectorService>();
builder.Services.AddSingleton<IInstrumentRepository, InstrumentRepository>();
builder.Services.AddSingleton<IQuoteManager, QuoteManager>();
builder.Services.AddHttpClient();
builder.Configuration.AddJsonFile("config.json", optional: false, reloadOnChange: false);
string launchProfileName = builder.Configuration.GetValue<string>("LAUNCH_PROFILE_NAME");
if (string.IsNullOrEmpty(launchProfileName))
{
    throw new ArgumentNullException("launch profile must be addressed first(live or testnet)");
}
builder.Services.Configure<List<FeedAdapterConfig>>(
    builder.Configuration.GetSection($"{launchProfileName}:feed:adapters")
);
builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<IOptions<List<FeedAdapterConfig>>>().Value
);

//--- Configure Serilog ---
// This replaces the default logging provider.
builder.Host.UseSerilog((context, services, configuration) =>
{
    var logPath = GetLogPath(launchProfileName); // Pass the profile name

    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Profile", launchProfileName) // Optional: for console logging
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Profile}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(logPath, rollingInterval: RollingInterval.Day);
});
var app = builder.Build();


// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Helper function to determine the log path based on the OS.
// Copied from the OMS project for consistency.
string GetLogPath(string profileName)
{
    var sanitizedProfileName = string.Join("_", profileName.Split(Path.GetInvalidFileNameChars()));
    var fileName = $"gui-{sanitizedProfileName}-.log"; // e.g., gui-live-.log

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        var dir = "/var/log/openhft";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }
    else
    {
        var dir = "logs";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }
}