using OpenHFT.GUI.Components;
using MudBlazor.Services;
using OpenHFT.GUI.Services;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Instruments;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddScoped<IOrderBookManager, MockOrderBookManager>();
builder.Services.AddSingleton<IOmsConnectorService, OmsConnectorService>();
builder.Services.AddSingleton<IInstrumentRepository, InstrumentRepository>();
builder.Configuration.AddJsonFile("config.json", optional: false, reloadOnChange: false);

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
