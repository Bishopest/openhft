using System;
using Microsoft.AspNetCore.Components;
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

    [Parameter]
    public int DisplayDepth { get; set; } = 30; // Number of ticks to show above and below the center

    // --- Parameters ---
    // Data passed from parent components is defined here.
    [Parameter, EditorRequired]
    public Instrument DisplayInstrument { get; set; }

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
    private async void HandleOrderBookUpdate(OrderBook snapshot)
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
        if (_currentOrderBook is null || DisplayInstrument is null) return;

        // 1. For fast lookups, convert the book's levels into dictionaries.
        var bidLookup = _currentOrderBook.Bids.ToDictionary(l => l.Price, l => l.TotalQuantity);
        var askLookup = _currentOrderBook.Asks.ToDictionary(l => l.Price, l => l.TotalQuantity);

        // 2. Determine the center price for our display grid.
        var (bestBid, _) = _currentOrderBook.GetBestBid();
        var (bestAsk, _) = _currentOrderBook.GetBestAsk();

        Price centerPrice;
        if (bestAsk.ToTicks() > 0)
            centerPrice = bestAsk; // Center around the best ask to keep it in view
        else if (bestBid.ToTicks() > 0)
            centerPrice = bestBid;
        else
            return; // No book data to display

        var tickSize = DisplayInstrument.TickSize;
        var newLevels = new List<DisplayLevel>();

        // 3. Build the grid from top (highest price) to bottom (lowest price).
        for (int i = DisplayDepth; i >= -DisplayDepth; i--)
        {
            var currentPrice = Price.FromTicks(centerPrice.ToTicks() + (tickSize.ToTicks() * i));

            // Find matching quantities from our lookups
            bidLookup.TryGetValue(currentPrice, out var bidQty);
            askLookup.TryGetValue(currentPrice, out var askQty);

            newLevels.Add(new DisplayLevel(currentPrice, bidQty.ToTicks() > 0 ? bidQty : null, askQty.ToTicks() > 0 ? askQty : null));
        }

        _displayLevels = newLevels;
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
