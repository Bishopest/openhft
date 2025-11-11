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
    private IOmsConnectorService OmsConnector { get; set; } = default!;
    [Inject]
    private ILogger<Home> Logger { get; set; } = default!;
    [Inject]
    private IExchangeFeedManager FeedManager { get; set; } = default!;

    /// <summary>
    /// A reference to the child QuotingParameterController component instance.
    /// </summary>
    private QuotingParametersController? _quotingController;
    /// <summary>
    /// Stores the active quoting parameters for each instrument ID.
    /// </summary>
    private List<InstanceStatusPayload> _activeInstances = new();
    private InstanceStatusPayload? _selectedInstance;
    // Keep track of which instruments we are subscribed to
    private readonly HashSet<int> _subscribedInstrumentIds = new();
    private bool _isDisposed = false;

    // --- Lifecycle Methods ---
    protected override void OnInitialized()
    {
        if (OmsConnector.CurrentStatus == ConnectionStatus.Connected)
        {
            // If we load the page and we are already connected,
            // immediately request the list of instances.
            RequestInstanceStatuses();
        }

        // Subscribe to the connector's status changes to keep our UI in sync
        OmsConnector.OnConnectionStatusChanged += HandleStatusChange;
        OmsConnector.OnInstanceStatusReceived += HandleInstanceStatusUpdate;
    }

    private async void HandleInstanceStatusUpdate(InstanceStatusEvent statusEvent)
    {
        if (_isDisposed)
        {
            return; // Do nothing if disposed.
        }
        var payload = statusEvent.Payload;
        Logger.LogInformationWithCaller($"Received status update for Instrument ID: {payload.InstrumentId}, Active: {payload.IsActive}");

        var existingInstance = _activeInstances.FirstOrDefault(i => i.InstrumentId == payload.InstrumentId);
        if (existingInstance != null)
        {
            // Update existing instance
            existingInstance.IsActive = payload.IsActive;
            existingInstance.Parameters = payload.Parameters;
        }
        else
        {
            // Add new instance
            _activeInstances.Add(payload);
            if (_subscribedInstrumentIds.Add(payload.InstrumentId))
            {
                Logger.LogInformationWithCaller($"New instance detected. Subscribing to market data for Instrument ID: {payload.InstrumentId}");
                await FeedManager.SubscribeToInstrumentAsync(payload.InstrumentId);
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    // Called when a user clicks a row in the InstanceListView
    private async Task HandleInstanceSelection(InstanceStatusPayload instance)
    {
        Logger.LogInformationWithCaller($"User selected instance for Instrument ID: {instance.InstrumentId}");

        _selectedInstance = instance;

        // Update child components with the data of the new selection
        if (_quotingController != null)
        {
            await _quotingController.UpdateParametersAsync(instance.Parameters);
        }

        StateHasChanged();
    }

    private async void HandleStatusChange(ConnectionStatus newStatus)
    {
        if (newStatus == ConnectionStatus.Disconnected)
        {
            // Clear all state when disconnected
            _activeInstances.Clear();
            _selectedInstance = null;
            _subscribedInstrumentIds.Clear();
        }
        else if (newStatus == ConnectionStatus.Connected)
        {
            RequestInstanceStatuses();
        }
        // Notify Blazor that the state has changed and the UI needs to re-render
        await InvokeAsync(StateHasChanged);
    }

    private void RequestInstanceStatuses()
    {
        _ = OmsConnector.SendCommandAsync(new GetInstanceStatusesCommand());
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

    /// <summary>
    /// This method is called when the QuotingParameterController's OnSubmit event is fired.
    /// </summary>
    private async Task HandleCancelParameters(int instrumentId)
    {
        if (OmsConnector.CurrentStatus != ConnectionStatus.Connected)
        {
            Snackbar.Add("Cannot retire strategy, not connected to OMS.", Severity.Error);
            return;
        }

        Logger.LogInformationWithCaller($"Retiring quoting instance for Instrument ID: {instrumentId}");

        // Wrap the parameters in the command object
        var command = new RetireInstanceCommand(instrumentId);

        // Send the command via the service
        await OmsConnector.SendCommandAsync(command);

        Snackbar.Add($"Retire command sent for Instrument ID {instrumentId}.", Severity.Success);
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
            OmsConnector.OnInstanceStatusReceived -= HandleInstanceStatusUpdate;
        }

        _isDisposed = true;
    }
}