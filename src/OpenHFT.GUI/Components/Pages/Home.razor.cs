using System;
using System.Reflection.Metadata;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;
using OpenHFT.GUI.Components.Shared;
using OpenHFT.GUI.Services;
using OpenHFT.Hedging;
using OpenHFT.Oms.Api.WebSocket;
using OpenHFT.Quoting;

namespace OpenHFT.GUI.Components.Pages;

public partial class Home : ComponentBase, IDisposable
{
    // --- Dependencies ---
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private IHedgingCacheService HedgingCache { get; set; } = default!;
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
    private HedgingParametersController? _hedgingController;
    // --- Centralized State ---
    private List<InstanceStatusPayload> _activeInstances = new();
    private InstanceStatusPayload? _selectedInstance;
    private readonly HashSet<int> _subscribedInstrumentIds = new();
    private HedgingStatusPayload? _hedgingStatus;
    private bool _isDisposed = false;

    // --- Lifecycle Methods ---
    protected override void OnInitialized()
    {
        _activeInstances = OrderCache.GetAllActiveInstances().ToList();

        OmsConnector.OnConnectionStatusChanged += HandleStatusChange;
        OrderCache.OnInstancesUpdated += HandleInstancesUpdated;
        HedgingCache.OnHedgingStatusUpdated += HandleHedgingStatusUpdate;
        FeedManager.AdapterConnectionStateChanged += HandleAdapterConnectionStateChanged;
    }

    private async void HandleHedgingStatusUpdate()
    {
        // Re-fetch status when cache is updated
        if (_selectedInstance != null)
        {
            _hedgingStatus = HedgingCache.GetHedgingStatus(_selectedInstance.OmsIdentifier, _selectedInstance.InstrumentId);
        }
        await InvokeAsync(StateHasChanged);
    }

    private async void HandleInstancesUpdated()
    {
        if (_isDisposed) return;
        _activeInstances = OrderCache.GetAllActiveInstances().ToList();
        if (_quotingController != null && _selectedInstance != null)
        {
            var selectedInstance = _activeInstances.FirstOrDefault(i => i.InstrumentId == _selectedInstance.InstrumentId && i.OmsIdentifier == _selectedInstance.OmsIdentifier);
            if (selectedInstance != null)
            {
                var serverConfig = Configuration.GetSection("oms").Get<List<OmsServerConfig>>()?
                                                .FirstOrDefault(s => s.OmsIdentifier == selectedInstance.OmsIdentifier);
                await _quotingController.UpdateParametersAsync(selectedInstance.Parameters, serverConfig);
            }
        }
        await InvokeAsync(StateHasChanged);
    }

    private void HandleAdapterConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (_isDisposed) return;

        if (!e.IsConnected && sender is IFeedAdapter adapter)
        {
            Logger.LogWarningWithCaller($"Connection to {adapter.SourceExchange} feed lost. Clearing relevant subscriptions from UI state.");

            var idsToRemove = _subscribedInstrumentIds
                .Where(id =>
                {
                    var instrument = InstrumentRepository.GetById(id);
                    return instrument != null && instrument.SourceExchange == adapter.SourceExchange;
                })
                .ToList();

            if (idsToRemove.Any())
            {
                Logger.LogInformationWithCaller($"Removing {idsToRemove.Count} subscribed instrument IDs from UI state due to {adapter.SourceExchange} disconnection: {string.Join(", ", idsToRemove)}");

                foreach (var id in idsToRemove)
                {
                    _subscribedInstrumentIds.Remove(id);
                }
            }
        }
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
        // if (_selectedInstance != null && _subscribedInstrumentIds.Remove(_selectedInstance.InstrumentId))
        // {
        //     await FeedManager.UnsubscribeFromInstrumentAsync(_selectedInstance.InstrumentId);
        // }

        _selectedInstance = instance;

        // Subscribe to the new selection's market data
        if (_subscribedInstrumentIds.Add(_selectedInstance.InstrumentId))
        {
            await FeedManager.SubscribeToInstrumentAsync(_selectedInstance.InstrumentId);
            var feedInst = InstrumentRepository.GetById(_selectedInstance.InstrumentId);
            if (feedInst is not null && feedInst.DenominationCurrency == Currency.BTC)
            {
                Logger.LogInformationWithCaller($"Instrument {feedInst.Symbol} requires BTC/USDT rate. Ensuring reference feed is subscribed");

                // Find the reference instrument used by FxRateManager.
                var referenceInstrument = InstrumentRepository.GetAll().FirstOrDefault(i =>
                    i.SourceExchange == GuiFxRateManager.ReferenceExchange &&
                    i.ProductType == GuiFxRateManager.ReferenceProductType &&
                    i.BaseCurrency == Currency.BTC &&
                    i.QuoteCurrency == Currency.USDT);

                if (referenceInstrument == null)
                {
                    Logger.LogWarningWithCaller("Could not find the reference BTC/USDT instrument for FX rate conversion.");
                }

                if (_subscribedInstrumentIds.Add(referenceInstrument.InstrumentId))
                {
                    await FeedManager.SubscribeToInstrumentAsync(referenceInstrument.InstrumentId);
                }
            }
        }

        if (_quotingController != null)
        {
            var serverConfig = Configuration.GetSection("oms").Get<List<OmsServerConfig>>()?
                                            .FirstOrDefault(s => s.OmsIdentifier == instance.OmsIdentifier);
            await _quotingController.UpdateParametersAsync(instance.Parameters, serverConfig);
        }

        _hedgingStatus = HedgingCache.GetHedgingStatus(instance.OmsIdentifier, instance.InstrumentId);
        if (_hedgingController != null)
        {
            await _hedgingController.UpdateParametersAsync(_hedgingStatus?.Parameters);
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

    private async Task HandleStartHedging(HedgingParameters parameters)
    {
        if (_selectedInstance is null) return;
        var targetServer = Configuration.GetSection("oms").Get<List<OmsServerConfig>>()?
                                        .FirstOrDefault(s => s.OmsIdentifier == _selectedInstance.OmsIdentifier);
        if (targetServer is null)
        {
            Snackbar.Add($"Could not find server config for OMS: {_selectedInstance.OmsIdentifier}", Severity.Error);
            return;
        }

        if (OmsConnector.GetStatus(targetServer) != ConnectionStatus.Connected)
        {
            Snackbar.Add($"Cannot start hedger, not connected to OMS: {_selectedInstance.OmsIdentifier}", Severity.Error);
            return;
        }

        var command = new UpdateHedgingParametersCommand(parameters);
        await OmsConnector.SendCommandAsync(targetServer, command);
        Snackbar.Add("Start Hedging command sent.", Severity.Info);
    }

    private async Task HandleStopHedging()
    {
        if (_selectedInstance is null) return;
        var targetServer = Configuration.GetSection("oms").Get<List<OmsServerConfig>>()?
                                        .FirstOrDefault(s => s.OmsIdentifier == _selectedInstance.OmsIdentifier);
        if (targetServer is null)
        {
            Snackbar.Add($"Could not find server config for OMS: {_selectedInstance.OmsIdentifier}", Severity.Error);
            return;
        }

        // Check connection status for the specific server
        if (OmsConnector.GetStatus(targetServer) != ConnectionStatus.Connected)
        {
            Snackbar.Add($"Cannot stop hedger, not connected to OMS: {_selectedInstance.OmsIdentifier}", Severity.Error);
            return;
        }

        Logger.LogInformationWithCaller($"Stop hedger for Instrument ID: {_selectedInstance.InstrumentId} on OMS: {_selectedInstance.OmsIdentifier}");

        var command = new StopHedgingCommand(_selectedInstance.InstrumentId);
        await OmsConnector.SendCommandAsync(targetServer, command);
        Snackbar.Add("Stop Hedging command sent.", Severity.Warning);
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
            HedgingCache.OnHedgingStatusUpdated -= HandleHedgingStatusUpdate;
            FeedManager.AdapterConnectionStateChanged -= HandleAdapterConnectionStateChanged;
        }

        _isDisposed = true;
    }
}