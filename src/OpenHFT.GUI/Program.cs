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
