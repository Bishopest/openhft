using System;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using OpenHFT.Core.Configuration;
using OpenHFT.GUI.Services;

namespace OpenHFT.GUI.Components.Shared;

public partial class OmsConnectionManager : ComponentBase
{
    [Parameter]
    public IEnumerable<OmsServerConfig> Servers { get; set; } = new List<OmsServerConfig>();

    [Parameter]
    public ConnectionStatus CurrentStatus { get; set; }

    [Parameter]
    public EventCallback<OmsServerConfig> OnConnect { get; set; }

    [Parameter]
    public EventCallback OnDisconnect { get; set; }

    private OmsServerConfig? _selectedServer;
    private bool IsConnectDisabled => _selectedServer is null || CurrentStatus == ConnectionStatus.Connected || CurrentStatus == ConnectionStatus.Connecting;
    private bool IsDisconnectDisabled => CurrentStatus != ConnectionStatus.Connected;

    private async Task HandleConnectClick()
    {
        if (_selectedServer is not null)
        {
            await OnConnect.InvokeAsync(_selectedServer);
        }
    }

    private async Task HandleDisconnectClick()
    {
        await OnDisconnect.InvokeAsync();
    }

    /// <summary>
    /// Converts the ConnectionStatus enum into a corresponding MudBlazor Color
    /// for display in the UI. This simplifies the Razor markup and avoids compiler confusion.
    /// </summary>
    /// <returns>A MudBlazor.Color enum value.</returns>
    private Color GetStatusColor()
    {
        return CurrentStatus switch
        {
            ConnectionStatus.Connected => Color.Success,
            ConnectionStatus.Connecting => Color.Info,
            ConnectionStatus.Error => Color.Error,
            _ => Color.Default // Disconnected or other states
        };
    }
}
