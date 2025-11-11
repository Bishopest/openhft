using System;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using OpenHFT.Core.Configuration;
using OpenHFT.GUI.Services;

namespace OpenHFT.GUI.Components.Layout;

public partial class Footer : ComponentBase, IDisposable
{
    [Inject]
    private IConfiguration Configuration { get; set; } = default!;
    [Inject]
    private IOmsConnectorService OmsConnector { get; set; } = default!;

    private bool _drawerOpen = true;

    // --- STATE FOR THE NEW FOOTER ---
    private List<OmsServerConfig> _omsServers = new();

    // NOTE: This assumes you will have one OmsConnectorService per OMS in the future.
    // For now, with one service, we'll just track one status, but the UI is ready for multiples.
    private OmsServerConfig? _connectedServer;
    private ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;

    protected override void OnInitialized()
    {
        // Load server list from config
        _omsServers = Configuration.GetSection("oms").Get<List<OmsServerConfig>>() ?? new List<OmsServerConfig>();
        _currentStatus = OmsConnector.CurrentStatus;

        // If already connected, pre-select the server in the dropdown
        if (_currentStatus == ConnectionStatus.Connected && OmsConnector.ConnectedServerUri != null)
        {
            string connectedUriString = OmsConnector.ConnectedServerUri.AbsoluteUri;
            _connectedServer = _omsServers.FirstOrDefault(s =>
            {
                if (Uri.TryCreate(s.Url, UriKind.Absolute, out var configUri))
                {
                    return configUri.AbsoluteUri == connectedUriString;
                }
                else
                {
                    return false;
                }
            });
        }

        // Subscribe to subsequent status changes
        OmsConnector.OnConnectionStatusChanged += HandleStatusChange;
    }

    private async Task HandleConnectClick(OmsServerConfig server)
    {
        // If another server is already connected, disconnect first.
        if (_currentStatus == ConnectionStatus.Connected && _connectedServer != server)
        {
            await OmsConnector.DisconnectAsync();
        }
        await OmsConnector.ConnectAsync(new Uri(server.Url));
    }

    private async Task HandleDisconnectClick()
    {
        await OmsConnector.DisconnectAsync();
    }

    private async void HandleStatusChange(ConnectionStatus newStatus)
    {
        _currentStatus = newStatus;
        if (newStatus == ConnectionStatus.Connected && OmsConnector.ConnectedServerUri != null)
        {
            string connectedUriString = OmsConnector.ConnectedServerUri.AbsoluteUri;
            _connectedServer = _omsServers.FirstOrDefault(s =>
            {
                if (Uri.TryCreate(s.Url, UriKind.Absolute, out var configUri))
                {
                    return configUri.AbsoluteUri == connectedUriString;
                }
                else
                {
                    return false;
                }
            });
        }
        else
        {
            _connectedServer = null;
        }
        await InvokeAsync(StateHasChanged);
    }

    private Color GetStatusColor(OmsServerConfig server)
    {
        if (_connectedServer != server) return Color.Default;
        return _currentStatus switch
        {
            ConnectionStatus.Connected => Color.Success,
            ConnectionStatus.Connecting => Color.Info,
            ConnectionStatus.Error => Color.Error,
            _ => Color.Default
        };
    }

    private void DrawerToggle()
    {
        _drawerOpen = !_drawerOpen;
    }

    public void Dispose()
    {
        OmsConnector.OnConnectionStatusChanged -= HandleStatusChange;
    }
}