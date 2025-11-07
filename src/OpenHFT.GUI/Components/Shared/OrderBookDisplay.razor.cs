using System;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.GUI.Services;

namespace OpenHFT.GUI.Components.Shared;

public partial class OrderBookDisplay : ComponentBase, IDisposable
{
    // A private record to represent a single row in our display grid.
    // It holds the price for the row and any bid/ask quantities that match that price.
    public record DisplayLevel(Price Price, Quantity? BidQuantity, Quantity? AskQuantity);

    // --- Dependencies ---
    // Services are injected here using the [Inject] attribute.
    [Inject]
    private IOrderBookManager OrderBookManager { get; set; } = default!;
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    [Parameter]
    public int DisplayDepth { get; set; } = 40; // Number of ticks to show above and below the center
    private decimal PriceGrouping => DisplayInstrument is not null ? DisplayInstrument.TickSize.ToDecimal() * _groupingMultipliers[_selectedMultiplierIndex] : 0.01m;

    // --- Parameters ---
    // Data passed from parent components is defined here.
    [Parameter, EditorRequired]
    public Instrument DisplayInstrument { get; set; }
    private string _priceFormat = "F2"; // Default format
    private string _quantityFormat = "N4"; // Default format


    // --- SLIDER STATE MANAGEMENT ---
    private readonly int[] _groupingMultipliers = { 1, 10, 50, 100 };
    private int _selectedMultiplierIndex = 0;
    private int SelectedMultiplierIndex
    {
        get => _selectedMultiplierIndex;
        set
        {
            if (_selectedMultiplierIndex != value)
            {
                _selectedMultiplierIndex = value;
                UpdateDisplayGrid();
            }
        }
    }

    // --- STATE MANAGEMENT FOR AUTO-SCROLL ---
    private bool _shouldScrollToCenter = true; // Start with true for the initial load
    private int _previousInstrumentId = 0;
    private readonly string _scrollContainerId = $"scroll-container-{Guid.NewGuid()}";

    // --- State ---
    // Private fields and properties that hold the component's state.
    private OrderBook? _currentOrderBook;
    private List<DisplayLevel> _displayLevels = new();
    private bool _isDisposed = false;

    // --- Lifecycle Methods ---
    // Logic to run when the component is initialized.
    protected override void OnInitialized()
    {
        // Subscribe to the data source.
        OrderBookManager.OnOrderBookUpdated += HandleOrderBookUpdate;
    }

    // --- Event Handlers ---
    private async void HandleOrderBookUpdate(object? sender, OrderBook snapshot)
    {
        if (_isDisposed) return;

        if (snapshot.InstrumentId != DisplayInstrument.InstrumentId)
        {
            return;
        }

        _currentOrderBook = snapshot;

        UpdateDisplayGrid(); // Rebuild the visual grid based on the new data
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// This is the core logic. It builds the visual grid based on the current order book state.
    /// </summary>
    private void UpdateDisplayGrid()
    {
        if (_currentOrderBook is null || DisplayInstrument is null || PriceGrouping <= 0) return;

        // --- 1. Grouping and Aggregation Logic ---
        var groupedBids = new Dictionary<decimal, Quantity>();
        var groupedAsks = new Dictionary<decimal, Quantity>();

        // Group Bids by flooring the price
        foreach (var level in _currentOrderBook.Bids)
        {
            var price = level.Price.ToDecimal();
            var groupedPrice = Math.Floor(price / PriceGrouping) * PriceGrouping;

            if (!groupedBids.TryAdd(groupedPrice, level.TotalQuantity))
            {
                groupedBids[groupedPrice] += level.TotalQuantity;
            }
        }

        // Group Asks by ceiling the price
        foreach (var level in _currentOrderBook.Asks)
        {
            var price = level.Price.ToDecimal();
            var groupedPrice = Math.Ceiling(price / PriceGrouping) * PriceGrouping;

            if (!groupedAsks.TryAdd(groupedPrice, level.TotalQuantity))
            {
                groupedAsks[groupedPrice] += level.TotalQuantity;
            }
        }

        // --- 2. Determine Center Price from GROUPED data ---
        var bestBidPrice = groupedBids.Keys.DefaultIfEmpty(0).Max();
        var bestAskPrice = groupedAsks.Keys.DefaultIfEmpty(0).Min();

        Price centerPriceDecimal;
        if (bestAskPrice > 0)
        {
            centerPriceDecimal = Price.FromDecimal(bestAskPrice);
        }
        else
        {
            centerPriceDecimal = Price.FromDecimal(bestBidPrice);
        }
        if (centerPriceDecimal.ToDecimal() <= 0) return; // No valid data to display

        // --- 3. Build the Display Grid using PriceGrouping as the step ---
        var newLevels = new List<DisplayLevel>();
        var groupingAsPrice = Price.FromDecimal(PriceGrouping);

        // Round the center price to the nearest grouping interval to keep the grid stable
        // var centerPrice = Price.FromDecimal(Math.Round(centerPriceDecimal / PriceGrouping) * PriceGrouping);

        for (int i = DisplayDepth; i >= -DisplayDepth; i--)
        {
            var currentPrice = Price.FromTicks(centerPriceDecimal.ToTicks() + (groupingAsPrice.ToTicks() * i));
            var currentPriceDecimal = currentPrice.ToDecimal();

            groupedBids.TryGetValue(currentPriceDecimal, out var bidQty);
            groupedAsks.TryGetValue(currentPriceDecimal, out var askQty);

            newLevels.Add(new DisplayLevel(currentPrice, bidQty.ToTicks() > 0 ? bidQty : null, askQty.ToTicks() > 0 ? askQty : null));
        }

        _displayLevels = newLevels;
    }

    /// <summary>
    /// This method is called when parameters are set. It's the perfect place
    /// to calculate derived state, like our format strings.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (DisplayInstrument != null)
        {
            // --- CHANGE DETECTION LOGIC ---
            // Check if the instrument has actually changed.
            if (DisplayInstrument.InstrumentId != _previousInstrumentId)
            {
                _previousInstrumentId = DisplayInstrument.InstrumentId;
                _shouldScrollToCenter = true; // Set the flag to scroll on next render
                _selectedMultiplierIndex = 0;
                // Clear old data immediately to show the "Awaiting data..." message
                // This prevents showing stale data from the previous instrument.
                _currentOrderBook = null;
                _displayLevels.Clear();
            }
            // Calculate and store the format strings based on the instrument's properties.
            _priceFormat = $"F{GetDecimalPlaces(PriceGrouping)}";
            _quantityFormat = $"N{GetDecimalPlaces(PriceGrouping)}";
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // If the flag is set and we have data to display, scroll to center.
        if (_shouldScrollToCenter && _displayLevels.Any())
        {
            await JSRuntime.InvokeVoidAsync("scrollToCenter", _scrollContainerId);
            _shouldScrollToCenter = false; // Reset the flag
        }
    }
    /// <summary>
    /// Calculates the number of decimal places in a decimal value.
    /// </summary>
    private static int GetDecimalPlaces(decimal n)
    {
        // This is a robust way to count decimal places, handling different cultures.
        string s = n.ToString(CultureInfo.InvariantCulture);
        int decimalPointIndex = s.IndexOf('.');
        if (decimalPointIndex == -1)
        {
            return 0;
        }
        return s.Length - decimalPointIndex - 1;
    }

    // --- Cleanup ---
    // IDisposable implementation to prevent memory leaks.
    public void Dispose()
    {
        if (_isDisposed) return;

        // It's crucial to unsubscribe from events to avoid memory leaks.
        OrderBookManager.OnOrderBookUpdated -= HandleOrderBookUpdate;
        _isDisposed = true;
    }
}
