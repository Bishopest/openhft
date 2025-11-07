using System;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.GUI.Components.Shared;
using OpenHFT.GUI.Services;
using OpenHFT.Oms.Api.WebSocket;
using OpenHFT.Quoting;

namespace OpenHFT.GUI.Components.Pages;

public partial class Home : ComponentBase, IDisposable
{
    // --- Dependencies ---
    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;
    [Inject]
    private IConfiguration Configuration { get; set; } = default!;
    [Inject]
    private IOmsConnectorService OmsConnector { get; set; } = default!;
    [Inject]
    private IOrderBookManager OrderBookManager { get; set; } = default!;
    [Inject]
    private ILogger<Home> Logger { get; set; } = default!;
    [Inject]
    private IExchangeFeedManager FeedManager { get; set; } = default!;

    // --- State for Child Components ---
    private List<OmsServerConfig> _omsServers = new();
    private ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;
    private Instrument? _displayInstrument;
    /// <summary>
    /// A reference to the child QuotingParameterController component instance.
    /// </summary>
    private QuotingParametersController? _quotingController;
    /// <summary>
    /// Stores the active quoting parameters for each instrument ID.
    /// Key: InstrumentId, Value: QuotingParameters
    /// </summary>
    private Dictionary<int, QuotingParameters> _activeInstances = new();

    // --- Lifecycle Methods ---
    protected override void OnInitialized()
    {
        // Load OMS server list from appsettings.json
        _omsServers = Configuration.GetSection("oms").Get<List<OmsServerConfig>>() ?? new List<OmsServerConfig>();

        // Create a test instrument to display
        _displayInstrument = new CryptoPerpetual(
            instrumentId: 1001, symbol: "BTCUSDT", exchange: ExchangeEnum.BINANCE,
            baseCurrency: Currency.BTC, quoteCurrency: Currency.USDT, tickSize: Price.FromDecimal(0.1m),
            lotSize: Quantity.FromDecimal(0.001m), multiplier: 1m, minOrderSize: Quantity.FromDecimal(0.001m)
        );

        // Subscribe to the connector's status changes to keep our UI in sync
        OmsConnector.OnConnectionStatusChanged += HandleStatusChange;
        OmsConnector.OnInstanceStatusReceived += HandleInstanceStatusUpdate;
    }

    private async Task HandleInstrumentSelected(Instrument instrument)
    {
        if (_displayInstrument?.InstrumentId == instrument.InstrumentId) return;

        if (_displayInstrument != null)
        {
            await FeedManager.UnsubscribeFromInstrumentAsync(_displayInstrument.InstrumentId);
        }

        _displayInstrument = instrument;
        StateHasChanged();

        if (_displayInstrument != null)
        {
            await FeedManager.SubscribeToInstrumentAsync(_displayInstrument.InstrumentId);
        }
    }

    private async void HandleInstanceStatusUpdate(InstanceStatusEvent statusEvent)
    {
        var payload = statusEvent.Payload;
        Logger.LogInformationWithCaller($"Received status update for Instrument ID: {payload.InstrumentId}, Active: {payload.IsActive}");
        if (payload.IsActive)
        {
            _activeInstances[payload.InstrumentId] = payload.Parameters;

            if (_quotingController != null && _displayInstrument?.InstrumentId == payload.InstrumentId)
            {
                await _quotingController.UpdateParametersAsync(payload.Parameters);
            }
        }
        else
        {
            _activeInstances.Remove(payload.InstrumentId);
        }

        await InvokeAsync(StateHasChanged);
    }

    // --- Event Handlers for OmsConnectionManager ---
    private async Task HandleConnectRequest(OmsServerConfig server)
    {
        Logger.LogInformationWithCaller($"Connect requested for {server.Name} at {server.Url}");
        await OmsConnector.ConnectAsync(new Uri(server.Url));
    }

    private async Task HandleDisconnectRequest()
    {
        Logger.LogInformationWithCaller("Disconnect requested.");
        await OmsConnector.DisconnectAsync();
    }

    private async void HandleStatusChange(ConnectionStatus newStatus)
    {
        _currentStatus = newStatus;
        // Notify Blazor that the state has changed and the UI needs to re-render
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// This method is called when the QuotingParameterController's OnSubmit event is fired.
    /// </summary>
    private async Task HandleSubmitParameters(QuotingParameters parameters)
    {
        if (OmsConnector.CurrentStatus != ConnectionStatus.Connected)
        {
            Snackbar.Add("Cannot deploy strategy, not connected to OMS.", Severity.Error);
            return;
        }

        Logger.LogInformationWithCaller($"Deploying quoting instance for Instrument ID: {parameters.InstrumentId}");

        // Wrap the parameters in the command object
        var command = new UpdateParametersCommand(parameters);

        // Send the command via the service
        await OmsConnector.SendCommandAsync(command);

        Snackbar.Add($"Deploy command sent for Instrument ID {parameters.InstrumentId}.", Severity.Success);
    }

    // --- Cleanup ---
    public void Dispose()
    {
        OmsConnector.OnInstanceStatusReceived -= HandleInstanceStatusUpdate;
        OmsConnector.OnConnectionStatusChanged -= HandleStatusChange;
    }
}