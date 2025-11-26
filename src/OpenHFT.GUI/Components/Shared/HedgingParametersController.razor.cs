using System;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Hedging;

namespace OpenHFT.GUI.Components.Shared;

public partial class HedgingParametersController : ComponentBase
{
    [Inject] private IInstrumentRepository InstrumentRepository { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Parameter] public EventCallback<HedgingParameters> OnStart { get; set; }
    [Parameter] public EventCallback OnStop { get; set; }

    [Parameter] public bool IsHedgerActive { get; set; }
    [Parameter] public Instrument? QuotingInstrument { get; set; } // To know which instrument this is for

    private MudForm _form = new();
    private ParameterFormModel _model = new();
    private IEnumerable<Instrument> _availableInstruments = Enumerable.Empty<Instrument>();
    private Instrument? _selectedHedgeInstrument;

    private class ParameterFormModel
    {
        public HedgeOrderType OrderType { get; set; }
        public decimal Size { get; set; } = 100m;
    }

    protected override void OnInitialized()
    {
        _availableInstruments = InstrumentRepository.GetAll();
    }

    public async Task UpdateParametersAsync(HedgingParameters? parameters)
    {
        if (parameters.HasValue)
        {
            var p = parameters.Value;
            _model.OrderType = p.OrderType;
            _model.Size = p.Size.ToDecimal();
            _selectedHedgeInstrument = _availableInstruments.FirstOrDefault(i => i.InstrumentId == p.InstrumentId);
        }
        else
        {
            // Reset to default if no active hedger
            _model = new ParameterFormModel();
            _selectedHedgeInstrument = null;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleStartClick()
    {
        await _form.Validate();
        if (QuotingInstrument is null)
        {
            Snackbar.Add("Please select a quoting instrument.");
            return;
        }
        if (!_form.IsValid || _selectedHedgeInstrument is null)
        {
            Snackbar.Add("Please select a hedge instrument and fill all fields.", Severity.Warning);
            return;
        }

        var parameters = new HedgingParameters(
            QuotingInstrument.InstrumentId,
            _selectedHedgeInstrument.InstrumentId,
            _model.OrderType,
            Quantity.FromDecimal(_model.Size)
        );

        await OnStart.InvokeAsync(parameters);
    }

    private async Task HandleStopClick()
    {
        await OnStop.InvokeAsync();
    }

    private Task<IEnumerable<Instrument>> SearchInstruments(string text, CancellationToken token)
        => Task.FromResult(_availableInstruments.Where(i => i.Symbol.Contains(text, System.StringComparison.OrdinalIgnoreCase)));

    private string ConvertInstrumentToString(Instrument? inst)
        => inst is not null ? $"{inst.Symbol} ({inst.SourceExchange})" : string.Empty;
}