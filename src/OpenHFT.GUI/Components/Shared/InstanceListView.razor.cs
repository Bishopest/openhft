using System;
using Microsoft.AspNetCore.Components;
using OpenHFT.Core.Interfaces;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Components.Shared;

public partial class InstanceListView : ComponentBase
{
    [Inject]
    private IInstrumentRepository InstrumentRepository { get; set; } = default!;
    /// <summary>
    /// The list of all active instances received from the parent.
    /// </summary>
    [Parameter]
    public List<InstanceStatusPayload> Instances { get; set; } = new();

    /// <summary>
    /// The currently selected instance, for highlighting.
    /// </summary>
    [Parameter]
    public InstanceStatusPayload? SelectedInstance { get; set; }

    /// <summary>
    /// Fired when the user clicks on a row in the table.
    /// </summary>
    [Parameter]
    public EventCallback<InstanceStatusPayload> OnInstanceSelected { get; set; }

    /// <summary>
    /// This method is now directly bound to the table's SelectedItemChanged event.
    /// It handles both updating the internal state and notifying the parent.
    /// </summary>
    private async Task OnRowClicked(InstanceStatusPayload instance)
    {
        // Update the internal state for the icon to display correctly.
        SelectedInstance = instance;

        // Notify the parent component about the selection change.
        await OnInstanceSelected.InvokeAsync(instance);

        // It's often good practice to call StateHasChanged() in a local handler,
        // though InvokeAsync might trigger it implicitly. This makes it explicit.
        StateHasChanged();
    }

    protected string GetSymbol(InstanceStatusPayload instance)
    {
        var instrument = InstrumentRepository.GetById(instance.InstrumentId);
        return instrument?.Symbol ?? instance.InstrumentId.ToString();
    }
    protected string GetOmsIdentifier(InstanceStatusPayload instance)
    {
        return instance.OmsIdentifier;
    }
    protected string GetExchange(InstanceStatusPayload instance)
    {
        var instrument = InstrumentRepository.GetById(instance.InstrumentId);
        return instrument?.SourceExchange.ToString() ?? "N/A";
    }
}