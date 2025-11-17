using System;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.GUI.Services;
using OpenHFT.Oms.Api.WebSocket;
using OpenHFT.Quoting.Models;

namespace OpenHFT.GUI.Components.Shared;

public partial class OrderBookDisplay : ComponentBase, IDisposable
{
    // A private record to represent a single row in our display grid.
    // It holds the price for the row and any bid/ask quantities that match that price.
    public record DisplayLevel(Price Price, Quantity? BidQuantity, Quantity? AskQuantity, bool IsMyQuoteBid, bool IsMyQuoteAsk);

    // --- Dependencies ---
    // Services are injected here using the [Inject] attribute.
    [Inject]
    private IOrderBookManager OrderBookManager { get; set; } = default!;
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;
    [Inject]
    private IQuoteManager QuoteManager { get; set; } = default!;
    [Inject]
    private IInstrumentRepository InstrumentRepository { get; set; } = default!;

    // --- Child Component References ---

    [Parameter]
    public int DisplayDepth { get; set; } = 40; // Number of ticks to show above and below the center
    private decimal PriceGrouping => DisplayInstrument is not null ? DisplayInstrument.TickSize.ToDecimal() * _groupingMultipliers[_selectedMultiplierIndex] : 0.01m;

    // --- Parameters ---
    // Data passed from parent components is defined here.
    [Parameter, EditorRequired]
    public InstanceStatusPayload ActiveInstance { get; set; }
    public Instrument? DisplayInstrument
    {
        get
        {
            if (ActiveInstance is null)
            {
                return null;
            }
            return InstrumentRepository.GetById(ActiveInstance.InstrumentId);
        }
    }
    private string _priceFormat = "F2"; // Default format
    private string _quantityFormat = "N4"; // Default format

    /// <summary>
    /// Calculates and formats the notional value of the theoretical Ask quote.
    /// Returns null if not applicable.
    /// </summary>
    protected string? ExpectedAskValue
    {
        get
        {
            if (_myCurrentQuote is null) return null;

            // This logic only applies if the instrument is a CryptoPerpetual
            if (DisplayInstrument is CryptoPerpetual future)
            {
                var ask = _myCurrentQuote.Value.Ask;
                // Formula: Price * Size * Multiplier
                var value = ask.Price.ToDecimal() * ask.Size.ToDecimal() * future.Multiplier;
                return ((long)value).ToString("N0"); // Format as a whole number with commas
            }

            return null; // Not a future, so no value to display
        }
    }

    /// <summary>
    /// Calculates and formats the notional value of the theoretical Bid quote.
    /// Returns null if not applicable.
    /// </summary>
    protected string? ExpectedBidValue
    {
        get
        {
            if (_myCurrentQuote is null) return null;

            if (DisplayInstrument is CryptoPerpetual future)
            {
                var bid = _myCurrentQuote.Value.Bid;
                var value = bid.Price.ToDecimal() * bid.Size.ToDecimal() * future.Multiplier;
                return ((long)value).ToString("N0");
            }

            return null;
        }
    }

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
    /// <summary>
    /// Holds the latest theoretical quote pair for the current instrument.
    /// </summary>
    private QuotePair? _myCurrentQuote;

    // --- STATE MANAGEMENT FOR AUTO-SCROLL ---
    private bool _shouldScrollToCenter = true; // Start with true for the initial load
    private int _previousInstrumentId = 0;
    private string _previousOmsIdentifier = "";

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
        QuoteManager.OnQuoteUpdated += HandleQuoteUpdate;
    }

    // --- Event Handlers ---
    private async void HandleOrderBookUpdate(object? sender, OrderBook snapshot)
    {
        if (_isDisposed) return;

        if (ActiveInstance is null)
        {
            return;
        }

        if (snapshot.InstrumentId != ActiveInstance.InstrumentId)
        {
            return;
        }

        _currentOrderBook = snapshot;

        UpdateDisplayGrid(); // Rebuild the visual grid based on the new data
        await InvokeAsync(StateHasChanged);
    }

    private async void HandleQuoteUpdate(object? sender, (string OmsIdentifier, QuotePair quotePair) args)
    {
        // If the quote update is for our instrument, we need to redraw the grid.
        if (ActiveInstance != null && args.quotePair.InstrumentId == ActiveInstance.InstrumentId && args.OmsIdentifier == ActiveInstance.OmsIdentifier)
        {
            _myCurrentQuote = args.quotePair;
            UpdateDisplayGrid();
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// This is the core logic. It builds the visual grid based on the current order book state.
    /// </summary>
    private void UpdateDisplayGrid()
    {
        if (_currentOrderBook is null || ActiveInstance is null || PriceGrouping <= 0) return;

        var myQuote = _myCurrentQuote;
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

        // --- 2. Find Best Bid/Ask from GROUPED data ---
        var bestBidPriceDecimal = groupedBids.Keys.DefaultIfEmpty(0).Max();
        var bestAskPriceDecimal = groupedAsks.Keys.DefaultIfEmpty(0).Min();

        // If there's no data, clear the display.
        if (bestBidPriceDecimal <= 0 && bestAskPriceDecimal <= 0)
        {
            _displayLevels = new List<DisplayLevel>();
            return;
        }

        // --- 3. Build Ask and Bid Levels Separately ---
        var newLevels = new List<DisplayLevel>();
        var groupingAsPrice = Price.FromDecimal(PriceGrouping);

        // Build Ask side: Start from Best Ask and go UP
        if (bestAskPriceDecimal > 0)
        {
            var startAskPrice = Price.FromDecimal(bestAskPriceDecimal);
            for (int i = 0; i < DisplayDepth; i++)
            {
                var currentPrice = Price.FromTicks(startAskPrice.ToTicks() + (groupingAsPrice.ToTicks() * i));
                var currentPriceDecimal = currentPrice.ToDecimal();

                bool isMyAsk = myQuote != null && Math.Ceiling(myQuote.Value.Ask.Price.ToDecimal() / PriceGrouping) * PriceGrouping == currentPriceDecimal;
                groupedAsks.TryGetValue(currentPriceDecimal, out var askQty);

                newLevels.Add(new DisplayLevel(currentPrice, null, askQty.ToTicks() > 0 ? askQty : null, false, isMyAsk));
            }
        }

        // Build Bid side: Start from Best Bid and go DOWN
        if (bestBidPriceDecimal > 0)
        {
            var startBidPrice = Price.FromDecimal(bestBidPriceDecimal);
            for (int i = 0; i < DisplayDepth; i++)
            {
                var currentPrice = Price.FromTicks(startBidPrice.ToTicks() - (groupingAsPrice.ToTicks() * i));
                var currentPriceDecimal = currentPrice.ToDecimal();

                bool isMyBid = myQuote != null && Math.Floor(myQuote.Value.Bid.Price.ToDecimal() / PriceGrouping) * PriceGrouping == currentPriceDecimal;
                groupedBids.TryGetValue(currentPriceDecimal, out var bidQty);

                // Avoid adding duplicates if spread is zero or crossed
                if (!newLevels.Any(l => l.Price == currentPrice))
                {
                    newLevels.Add(new DisplayLevel(currentPrice, bidQty.ToTicks() > 0 ? bidQty : null, null, isMyBid, false));
                }
            }
        }

        // --- 4. Sort and assign the final list ---
        // Sort all collected levels by price descending.
        _displayLevels = newLevels.OrderByDescending(l => l.Price).ToList();
    }

    /// <summary>
    /// This method is called when parameters are set. It's the perfect place
    /// to calculate derived state, like our format strings.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (ActiveInstance != null)
        {
            // Check if the instrument has actually changed.
            if (ActiveInstance.InstrumentId != _previousInstrumentId || ActiveInstance.OmsIdentifier != _previousOmsIdentifier)
            {
                _previousInstrumentId = ActiveInstance.InstrumentId;
                _previousOmsIdentifier = ActiveInstance.OmsIdentifier;
                _shouldScrollToCenter = true;
                _selectedMultiplierIndex = 0;
                _currentOrderBook = null;
                _displayLevels.Clear();

                var currentQuote = QuoteManager.GetQuote(ActiveInstance.OmsIdentifier, ActiveInstance.InstrumentId);
                if (currentQuote != null)
                {
                    _myCurrentQuote = currentQuote;
                }
                else
                {
                    _myCurrentQuote = null;
                }
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
