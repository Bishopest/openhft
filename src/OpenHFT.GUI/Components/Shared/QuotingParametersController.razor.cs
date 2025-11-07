using System;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Quoting;

namespace OpenHFT.GUI.Components.Shared;

public partial class QuotingParametersController
{
    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;
    [Inject]
    private IInstrumentRepository InstrumentRepository { get; set; } = default!;

    /// <summary>
    /// This event is triggered when the user clicks the Submit button with valid data.
    /// </summary>
    [Parameter]
    public EventCallback<QuotingParameters> OnSubmit { get; set; }

    private MudForm _form = new();
    private ParameterFormModel _model = new(); // A temporary model for form binding

    private IEnumerable<Instrument> _availableInstruments = Enumerable.Empty<Instrument>();
    private Instrument? _selectedInstrument;
    private Instrument? _selectedFvSourceInstrument;

    // A private class to hold form data. This is often cleaner than binding directly to a struct.
    private class ParameterFormModel
    {
        public int InstrumentId { get; set; } = 1001;
        public FairValueModel FvModel { get; set; }
        public int FairValueSourceInstrumentId { get; set; }
        public decimal SpreadBp { get; set; } = 2.5m;
        public decimal SkewBp { get; set; } = 0.5m;
        public decimal Size { get; set; } = 0.01m;
        public int Depth { get; set; } = 5;
        public QuoterType Type { get; set; }
    }

    protected override void OnInitialized()
    {
        _availableInstruments = InstrumentRepository.GetAll();
        _selectedInstrument = _availableInstruments.FirstOrDefault();
    }

    /// <summary>
    /// This function is called by the MudAutocomplete component whenever the user types.
    /// It filters the list of available instruments based on the search text.
    /// </summary>
    private async Task<IEnumerable<Instrument>> SearchInstruments(string searchText, CancellationToken cancellationToken)
    {
        // Simulate a small delay to avoid excessive searching on fast typing
        await Task.Delay(5);

        if (string.IsNullOrWhiteSpace(searchText))
        {
            // If the search box is empty, show the first 10 instruments as a suggestion.
            return _availableInstruments.Take(10);
        }

        // Perform a case-insensitive search on the instrument symbol.
        return _availableInstruments.Where(i =>
            i.Symbol.Contains(searchText, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// This function tells the MudAutocomplete how to display a selected Instrument.
    /// </summary>
    private string ConvertInstrumentToString(Instrument? instrument)
    {
        return instrument is not null ? $"{instrument.Symbol} ({instrument.SourceExchange}) ({instrument.ProductType})" : string.Empty;
    }

    private async Task HandleSubmit()
    {
        await _form.Validate();
        if (!_form.IsValid || _selectedInstrument is null)
        {
            Snackbar.Add("Please select an instrument and fill all required fields.", Severity.Warning);
            return;
        }

        // Convert the form model to the actual QuotingParameters struct
        var parameters = new QuotingParameters(
            _selectedInstrument.InstrumentId,
            _model.FvModel,
            _selectedFvSourceInstrument?.InstrumentId ?? 0,
            _model.SpreadBp,
            _model.SkewBp,
            Quantity.FromDecimal(_model.Size), // Convert decimal to Quantity
            _model.Depth,
            _model.Type
        );

        // Invoke the callback to notify the parent component
        await OnSubmit.InvokeAsync(parameters);
    }

    private void HandleCancel()
    {
        _model = new ParameterFormModel(); // Reset the form
        _selectedInstrument = _availableInstruments.FirstOrDefault();
        _selectedFvSourceInstrument = null;
        _form.ResetValidation();
        Snackbar.Add("Form cleared.", Severity.Info);
    }
}
