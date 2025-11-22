using System;
using System.Text.Json;
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
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private IOmsConnectorService OmsConnector { get; set; } = default!;
    [Inject] private IInstrumentRepository InstrumentRepository { get; set; } = default!;
    [Inject] private IExchangeFeedManager FeedManager { get; set; } = default!;
    [Inject] private IOrderCacheService OrderCache { get; set; } = default!;
    [Inject] private IBookCacheService BookCache { get; set; } = default!;
    [Inject] private ILogger<Home> Logger { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private JsonSerializerOptions _jsonOptions { get; set; } = default!;

    // --- Child Component References ---
    private QuotingParametersController? _quotingController;
    // --- Centralized State ---
    private List<InstanceStatusPayload> _activeInstances = new();
    private InstanceStatusPayload? _selectedInstance;
    private readonly HashSet<int> _subscribedInstrumentIds = new();
    private bool _isDisposed = false;

    // --- Lifecycle Methods ---
    protected override void OnInitialized()
    {
        _activeInstances = OrderCache.GetAllActiveInstances().ToList();

        var connectedOmsConfig = OmsConnector.GetConnectedServers();
        OmsConnector.OnConnectionStatusChanged += HandleStatusChange;
        OrderCache.OnInstancesUpdated += HandleInstancesUpdated;
    }

    private async void HandleInstancesUpdated()
    {
        if (_isDisposed) return;
        _activeInstances = OrderCache.GetAllActiveInstances().ToList();
        await InvokeAsync(StateHasChanged);
    }

    private async void HandleStatusChange((OmsServerConfig Server, ConnectionStatus Status) args)
    {
        if (_isDisposed) return;

        if (args.Status == ConnectionStatus.Disconnected || args.Status == ConnectionStatus.Error)
        {
            if (_selectedInstance?.OmsIdentifier == args.Server.OmsIdentifier)
            {
                _selectedInstance = null;
            }
            Logger.LogInformationWithCaller($"Connection to {args.Server.OmsIdentifier} lost. Clearing related state.");
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleInstanceSelection(InstanceStatusPayload instance)
    {
        if (_selectedInstance?.InstrumentId == instance.InstrumentId && _selectedInstance?.OmsIdentifier == instance.OmsIdentifier)
            return;

        Logger.LogInformationWithCaller($"User selected instance for Instrument: {instance.InstrumentId} on OMS: {instance.OmsIdentifier}");

        // Unsubscribe from the old selection's market data
        if (_selectedInstance != null && _subscribedInstrumentIds.Remove(_selectedInstance.InstrumentId))
        {
            await FeedManager.UnsubscribeFromInstrumentAsync(_selectedInstance.InstrumentId);
        }

        _selectedInstance = instance;

        // Subscribe to the new selection's market data
        if (_subscribedInstrumentIds.Add(_selectedInstance.InstrumentId))
        {
            await FeedManager.SubscribeToInstrumentAsync(_selectedInstance.InstrumentId);
        }

        if (_quotingController != null)
        {
            var serverConfig = Configuration.GetSection("oms").Get<List<OmsServerConfig>>()?
                                            .FirstOrDefault(s => s.OmsIdentifier == instance.OmsIdentifier);
            await _quotingController.UpdateParametersAsync(instance.Parameters, serverConfig);
        }

        StateHasChanged();
    }

    /// <summary>
    /// This method is called when the QuotingParameterController's OnSubmit event is fired.
    /// </summary>
    private async Task HandleSubmitParameters((string OmsIdentifier, QuotingParameters Parameters) args)
    {
        var targetServer = Configuration.GetSection("oms").Get<List<OmsServerConfig>>()?
                                        .FirstOrDefault(s => s.OmsIdentifier == args.OmsIdentifier);
        if (targetServer is null)
        {
            Snackbar.Add($"Could not find server config for OMS: {args.OmsIdentifier}", Severity.Error);
            return;
        }

        // Check connection status for the specific server
        if (OmsConnector.GetStatus(targetServer) != ConnectionStatus.Connected)
        {
            Snackbar.Add($"Cannot deploy instance, not connected to OMS: {args.OmsIdentifier}", Severity.Error);
            return;
        }

        Logger.LogInformationWithCaller($"Deploying/updating parameters for Instrument ID: {args.Parameters.InstrumentId} on OMS: {args.OmsIdentifier}");

        var command = new UpdateParametersCommand(args.Parameters);
        await OmsConnector.SendCommandAsync(targetServer, command);
        Snackbar.Add($"Update command sent to {targetServer.OmsIdentifier}.", Severity.Success);
    }

    /// <summary>
    /// This method is called when the QuotingParameterController's OnSubmit event is fired.
    /// </summary>
    private async Task HandleCancelParameters((string OmsIdentifier, int InstrumentId) args)
    {
        var targetServer = Configuration.GetSection("oms").Get<List<OmsServerConfig>>()?
                                        .FirstOrDefault(s => s.OmsIdentifier == args.OmsIdentifier);
        if (targetServer is null)
        {
            Snackbar.Add($"Could not find server config for OMS: {args.OmsIdentifier}", Severity.Error);
            return;
        }

        // Check connection status for the specific server
        if (OmsConnector.GetStatus(targetServer) != ConnectionStatus.Connected)
        {
            Snackbar.Add($"Cannot retire instance, not connected to OMS: {args.OmsIdentifier}", Severity.Error);
            return;
        }

        Logger.LogInformationWithCaller($"Retire instance for Instrument ID: {args.InstrumentId} on OMS: {args.OmsIdentifier}");

        var command = new RetireInstanceCommand(args.InstrumentId);
        await OmsConnector.SendCommandAsync(targetServer, command);
        Snackbar.Add($"Retire command sent to {targetServer.OmsIdentifier}.", Severity.Success);
    }
    // --- Cleanup ---
    public void Dispose()
    {
        // Standard dispose pattern
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            // Unsubscribe from all events here.
            OmsConnector.OnConnectionStatusChanged -= HandleStatusChange;
            OrderCache.OnInstancesUpdated -= HandleInstancesUpdated;
        }

        _isDisposed = true;
    }
}