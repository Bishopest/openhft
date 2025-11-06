using System;
using Microsoft.AspNetCore.Components;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.GUI.Services;

namespace OpenHFT.GUI.Components.Pages;

public partial class Home : ComponentBase, IDisposable
{
    // --- Dependencies ---
    [Inject]
    private IConfiguration Configuration { get; set; } = default!;
    [Inject]
    private IOmsConnectorService OmsConnector { get; set; } = default!;
    [Inject]
    private IOrderBookManager OrderBookManager { get; set; } = default!;
    [Inject]
    private ILogger<Home> Logger { get; set; } = default!;

    // --- State for Child Components ---
    private List<OmsServerConfig> _omsServers = new();
    private ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;
    private Instrument? _testInstrument;

    // --- Lifecycle Methods ---
    protected override void OnInitialized()
    {
        // Load OMS server list from appsettings.json
        _omsServers = Configuration.GetSection("oms").Get<List<OmsServerConfig>>() ?? new List<OmsServerConfig>();

        // Create a test instrument to display
        _testInstrument = new CryptoPerpetual(
            instrumentId: 1001, symbol: "BTCUSDT", exchange: ExchangeEnum.BINANCE,
            baseCurrency: Currency.BTC, quoteCurrency: Currency.USDT, tickSize: Price.FromDecimal(0.1m),
            lotSize: Quantity.FromDecimal(0.001m), multiplier: 1m, minOrderSize: Quantity.FromDecimal(0.001m)
        );

        // Subscribe to the connector's status changes to keep our UI in sync
        OmsConnector.OnConnectionStatusChanged += HandleStatusChange;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await OrderBookManager.ConnectAndSubscribeAsync(_testInstrument.InstrumentId);
        }
    }

    // --- Event Handlers for OmsConnectionManager ---
    private async Task HandleConnectRequest(OmsServerConfig server)
    {
        Logger.LogInformationWithCaller($"Connect requested for {server.Name} at {server.Url}");
        await OmsConnector.ConnectAsync(new Uri(server.Url));

        // Once connected, we can subscribe to the order book.
        if (OmsConnector.CurrentStatus == ConnectionStatus.Connected && _testInstrument != null)
        {
            // This replaces the old OnAfterRenderAsync logic.
            await OrderBookManager.ConnectAndSubscribeAsync(_testInstrument.InstrumentId);
        }
    }

    private async Task HandleDisconnectRequest()
    {
        Logger.LogInformationWithCaller("Disconnect requested.");
        await OmsConnector.DisconnectAsync();
        // Optionally, also tell the OrderBookManager to disconnect/clear
        await OrderBookManager.DisconnectAsync();
    }

    private async void HandleStatusChange(ConnectionStatus newStatus)
    {
        _currentStatus = newStatus;
        // Notify Blazor that the state has changed and the UI needs to re-render
        await InvokeAsync(StateHasChanged);
    }

    // --- Cleanup ---
    public void Dispose()
    {
        OmsConnector.OnConnectionStatusChanged -= HandleStatusChange;
    }
}