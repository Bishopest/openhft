using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting.FairValue;

public abstract class AbstractFairValueProvider : IFairValueProvider
{
    protected readonly ILogger Logger;
    protected readonly int InstrumentId;
    private Price? _lastFairValue;
    public Price? LastFairValue => _lastFairValue;

    public abstract FairValueModel Model { get; }
    public event EventHandler<FairValueUpdate>? FairValueChanged;

    protected AbstractFairValueProvider(ILogger logger, int instrumentId)
    {
        Logger = logger;
        InstrumentId = instrumentId;
        _lastFairValue = null;
    }

    /// <summary>
    /// Template method that orchestrates the fair value update process.
    /// It calls the abstract CalculateFairValue method and handles event firing.
    /// </summary>
    /// <param name="orderBook">The latest order book data.</param>
    public void Update(OrderBook orderBook)
    {
        if (orderBook.InstrumentId != InstrumentId)
        {
            Logger.LogWarningWithCaller($"Received an order book for a wrong instrument. Expected {InstrumentId}, got {orderBook.InstrumentId}");
            return;
        }

        // 1. Delegate the actual calculation to the concrete implementation.
        var newFairValue = CalculateFairValue(orderBook);

        if (!newFairValue.HasValue)
        {
            return;
        }

        // 2. Handle the common logic: check for changes and fire the event.
        if (newFairValue.Value != _lastFairValue)
        {
            _lastFairValue = newFairValue.Value;
            OnFairValueChanged(new FairValueUpdate(InstrumentId, _lastFairValue.Value));
        }
    }

    /// <summary>
    /// Concrete implementations must override this method to provide their specific fair value calculation logic.
    /// </summary>
    /// <param name="orderBook">The order book to calculate the fair value from.</param>
    /// <returns>The calculated fair value. Should return Price.FromTicks(0) if not calculable.</returns>
    protected abstract Price? CalculateFairValue(OrderBook orderBook);

    /// <summary>
    /// Helper method to safely invoke the event.
    /// </summary>
    protected virtual void OnFairValueChanged(FairValueUpdate update)
    {
        FairValueChanged?.Invoke(this, update);
    }
}
