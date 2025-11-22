using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.GUI.Services;
using OpenHFT.Quoting;

namespace OpenHFT.GUI.Components.Shared;

public partial class QuotingParametersController
{
    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;
    [Inject]
    private IInstrumentRepository InstrumentRepository { get; set; } = default!;
    [Inject]
    private IBookCacheService BookCache { get; set; } = default!;

    [Inject] private IConfiguration Configuration { get; set; } = default!;
    /// <summary>
    /// This event is triggered when the user clicks the Submit button with valid data.
    /// </summary>
    [Parameter] public EventCallback<(string OmsIdentifier, QuotingParameters Parameters)> OnSubmit { get; set; }
    [Parameter] public EventCallback<(string OmsIdentifier, int InstrumentId)> OnCancel { get; set; }

    [Parameter]
    public EventCallback<Instrument> OnInstrumentSelected { get; set; }

    /// <summary>
    /// Receives a boolean from the parent indicating if the strategy is active.
    /// </summary>
    [Parameter]
    public bool IsStrategyActive { get; set; }
    private MudForm _form = new();
    private ParameterFormModel _model = new(); // A temporary model for form binding
    private List<OmsServerConfig> _availableOmsServers = new();
    private string? _selectedOmsIdentifier;
    private IEnumerable<Instrument> _availableInstruments = Enumerable.Empty<Instrument>();
    private IEnumerable<string> _availableBookNames = Enumerable.Empty<string>();
    private Instrument? _selectedInstrument;
    private Instrument? SelectedInstrument
    {
        get => _selectedInstrument;
        set
        {
            if (_selectedInstrument != value)
            {
                _selectedInstrument = value;
                OnInstrumentSelected.InvokeAsync(value);
            }
        }
    }

    private string? SelectedBookName
    {
        get => _model.BookName;
        set
        {
            _model.BookName = value ?? string.Empty;
            ValidateBookSelection();
        }
    }

    private string? SelectedOmsIdentifier
    {
        get => _selectedOmsIdentifier;
        set
        {
            if (_selectedOmsIdentifier != value)
            {
                _selectedOmsIdentifier = value;
                _ = UpdateAvailableBooks();
                ValidateBookSelection();
            }
        }
    }

    private Instrument? _selectedFvSourceInstrument;

    // A private class to hold form data. This is often cleaner than binding directly to a struct.
    private class ParameterFormModel
    {
        public string BookName { get; set; }
        public FairValueModel FvModel { get; set; }
        public decimal AskSpreadBp { get; set; } = 2.5m;
        public decimal BidSpreadBp { get; set; } = -2.5m;
        public decimal SkewBp { get; set; } = 0.5m;
        public decimal Size { get; set; } = 0.01m;
        public int Depth { get; set; } = 5;
        public QuoterType Type { get; set; }
        public bool PostOnly { get; set; }
        public decimal MaxCumAskFills { get; set; } = 10000m;
        public decimal MaxCumBidFills { get; set; } = 10000m;
    }

    protected override void OnInitialized()
    {
        _availableInstruments = InstrumentRepository.GetAll();
        _selectedInstrument = _availableInstruments.FirstOrDefault();
        _availableOmsServers = Configuration.GetSection("oms").Get<List<OmsServerConfig>>() ?? new List<OmsServerConfig>();
        _selectedOmsIdentifier = _availableOmsServers.FirstOrDefault()?.OmsIdentifier;

        _ = UpdateAvailableBooks();
    }

    /// <summary>
    /// Updates the list of available books based on the selected OMS.
    /// </summary>
    private async Task UpdateAvailableBooks()
    {
        _availableBookNames = BookCache.GetBookNames(SelectedOmsIdentifier);

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Checks if the currently selected book is valid for the selected OMS.
    /// </summary>
    private bool ValidateBookSelection()
    {
        if (string.IsNullOrEmpty(_model.BookName)) return false;
        if (string.IsNullOrEmpty(SelectedOmsIdentifier)) return false;

        if (!_availableBookNames.Contains(_model.BookName))
        {
            Snackbar.Add($"Warning: Book '{_model.BookName}' is not available on OMS '{SelectedOmsIdentifier}'.", Severity.Warning);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Public method that can be called by a parent component to update the form's data.
    /// </summary>
    public async Task UpdateParametersAsync(QuotingParameters newParameters, OmsServerConfig? targetOms)
    {
        _model.BookName = newParameters.BookName;
        _model.FvModel = newParameters.FvModel;
        _model.AskSpreadBp = newParameters.AskSpreadBp;
        _model.BidSpreadBp = newParameters.BidSpreadBp;
        _model.SkewBp = newParameters.SkewBp;
        _model.Size = newParameters.Size.ToDecimal();
        _model.Depth = newParameters.Depth;
        _model.Type = newParameters.Type;
        _model.PostOnly = newParameters.PostOnly;
        _model.MaxCumAskFills = newParameters.MaxCumAskFills.ToDecimal();
        _model.MaxCumBidFills = newParameters.MaxCumBidFills.ToDecimal();

        // We need to update the instrument selection as well
        SelectedInstrument = _availableInstruments.FirstOrDefault(i => i.InstrumentId == newParameters.InstrumentId);
        _selectedFvSourceInstrument = _availableInstruments.FirstOrDefault(i => i.InstrumentId == newParameters.FairValueSourceInstrumentId);
        _selectedOmsIdentifier = targetOms?.OmsIdentifier;
        // Notify Blazor that the state has changed and the UI needs to re-render
        await InvokeAsync(StateHasChanged);
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

    private void TogglePostOnly(bool isPostOnly)
    {
        _model.PostOnly = isPostOnly;
    }

    private async Task HandleSubmit()
    {
        await _form.Validate();
        if (!_form.IsValid || _selectedInstrument is null)
        {
            Snackbar.Add("Please select an instrument and fill all required fields.", Severity.Warning);
            return;
        }

        if (string.IsNullOrEmpty(_selectedOmsIdentifier))
        {
            Snackbar.Add("Please select a target OMS server.", Severity.Warning);
            return;
        }

        if (!ValidateBookSelection())
        {
            return;
        }

        // Convert the form model to the actual QuotingParameters struct
        var parameters = new QuotingParameters(
            _selectedInstrument.InstrumentId,
            _model.BookName,
            _model.FvModel,
            _selectedFvSourceInstrument?.InstrumentId ?? 0,
            _model.AskSpreadBp,
            _model.BidSpreadBp,
            _model.SkewBp,
            Quantity.FromDecimal(_model.Size), // Convert decimal to Quantity
            _model.Depth,
            _model.Type,
            _model.PostOnly,
            Quantity.FromDecimal(_model.MaxCumBidFills),
            Quantity.FromDecimal(_model.MaxCumAskFills)
        );

        // Invoke the callback to notify the parent component
        await OnSubmit.InvokeAsync((_selectedOmsIdentifier, parameters));
    }

    private async Task HandleCancel()
    {
        if (string.IsNullOrEmpty(_selectedOmsIdentifier))
        {
            Snackbar.Add("Please select a target OMS server.", Severity.Warning);
            return;
        }

        if (_selectedInstrument is not null)
        {
            await OnCancel.InvokeAsync((_selectedOmsIdentifier, _selectedInstrument.InstrumentId));
        }

        _form.ResetValidation();
    }

    private string AskSpreadValidation(decimal askValue)
    {
        if (askValue <= _model.BidSpreadBp)
        {
            return "Ask Spread must be strictly greater than Bid Spread.";
        }
        return null; // 유효함
    }
    private string BidSpreadValidation(decimal askValue)
    {
        if (askValue >= _model.AskSpreadBp)
        {
            return "Bid Spread must be strictly greater than Bid Spread.";
        }
        return null; // 유효함
    }
}
