using System;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Utils;
using OpenHFT.GUI.Services;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Components.Layout;

public partial class Footer : ComponentBase, IDisposable
{
    [Inject]
    private IConfiguration Configuration { get; set; } = default!;
    [Inject]
    private IOmsConnectorService OmsConnector { get; set; } = default!;
    [Inject]
    private ILogger<Footer> Logger { get; set; } = default!;

    private List<OmsServerConfig> _omsServers = new();

    // --- KEY CHANGE: We only need a dictionary to track the status of each server ---
    private readonly Dictionary<string, ConnectionStatus> _statuses = new();

    protected override void OnInitialized()
    {
        // Load the server list from config.
        _omsServers = Configuration.GetSection("oms").Get<List<OmsServerConfig>>() ?? new List<OmsServerConfig>();

        // Initialize the status for each server by asking the central service.
        foreach (var server in _omsServers)
        {
            _statuses[server.OmsIdentifier] = OmsConnector.GetStatus(server);
        }

        // Subscribe to the single, centralized status change event.
        OmsConnector.OnConnectionStatusChanged += HandleStatusChange;
    }

    // Connect button now calls the central service with the server config.
    private async Task HandleConnectClick(OmsServerConfig serverConfig)
    {
        await OmsConnector.ConnectAsync(serverConfig);
    }

    // Disconnect button also calls the central service.
    private async Task HandleDisconnectClick(OmsServerConfig serverConfig)
    {
        await OmsConnector.DisconnectAsync(serverConfig);
    }

    // The event handler now receives a tuple with the server and its new status.
    private async void HandleStatusChange((OmsServerConfig Server, ConnectionStatus Status) args)
    {
        _statuses[args.Server.OmsIdentifier] = args.Status;

        if (args.Status == ConnectionStatus.Connected)
        {
            Logger.LogInformationWithCaller($"Connection to {args.Server.OmsIdentifier} established. Requesting initial state.");
            // Request initial state from the newly connected server.
            await OmsConnector.SendCommandAsync(args.Server, new GetInstanceStatusesCommand());
            await OmsConnector.SendCommandAsync(args.Server, new GetActiveOrdersCommand());
            await OmsConnector.SendCommandAsync(args.Server, new GetFillsCommand());
            await OmsConnector.SendCommandAsync(args.Server, new GetBookUpdateCommand());
        }

        await InvokeAsync(StateHasChanged);
    }

    // The color logic now just looks up the status from our dictionary.
    private Color GetStatusColor(OmsServerConfig serverConfig)
    {
        if (!_statuses.TryGetValue(serverConfig.OmsIdentifier, out var status))
            return Color.Default;

        return status switch
        {
            ConnectionStatus.Connected => Color.Success,
            ConnectionStatus.Connecting => Color.Info,
            ConnectionStatus.Error => Color.Error,
            _ => Color.Default
        };
    }

    public void Dispose()
    {
        OmsConnector.OnConnectionStatusChanged -= HandleStatusChange;
    }
}
