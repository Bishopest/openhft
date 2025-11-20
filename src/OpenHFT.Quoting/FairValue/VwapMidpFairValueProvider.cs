using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.OrderBook;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting.FairValue;

public class VwapMidpFairValueProvider : AbstractFairValueProvider, IOrderBookConsumerProvider
{
    public VwapMidpFairValueProvider(ILogger logger, int instrumentId) : base(logger, instrumentId)
    {
    }

    /// <summary>
    /// Calculates the Volume-Weighted Average Price for a given collection of price levels.
    /// </summary>
    /// <param name="levels">A collection of PriceLevel objects.</param>
    /// <returns>The calculated VWAP as a Price, or null if the input is empty or invalid.</returns>
    private Price? CalculateVwapForSide(IEnumerable<PriceLevel> levels)
    {
        decimal totalVolume = 0;
        decimal totalNotional = 0; // Notional = Price * Volume

        foreach (var level in levels)
        {
            var price = level.Price.ToDecimal();
            var volume = level.TotalQuantity.ToDecimal();

            if (volume <= 0) continue;

            totalNotional += price * volume;
            totalVolume += volume;
        }

        if (totalVolume == 0)
        {
            return null; // Cannot calculate VWAP if there is no volume.
        }

        // Return the VWAP as a Price object.
        return Price.FromDecimal(totalNotional / totalVolume);
    }

    /// <summary>
    /// Implements the specific calculation logic for the VWAP Mid-Price model.
    /// </summary>
    /// <param name="orderBook">The order book to calculate the fair value from.</param>
    /// <returns>The calculated VWAP-based fair value, or a zero price if not calculable.</returns>
    protected Price? CalculateFairValue(OrderBook orderBook)
    {
        // 2. Calculate the VWAP for each side.
        var bidVwap = CalculateVwapForSide(orderBook.Bids);
        var askVwap = CalculateVwapForSide(orderBook.Asks);

        // 3. If either side is not calculable, we cannot determine a fair value.
        if (!bidVwap.HasValue || !askVwap.HasValue)
        {
            return null;
        }

        // 4. The fair value is the mid-point of the bid-vwap and ask-vwap.
        var fairValue = (bidVwap.Value.ToDecimal() + askVwap.Value.ToDecimal()) / 2;

        return Price.FromDecimal(fairValue);
    }

    public override FairValueModel Model => FairValueModel.VwapMidp;

    public void Update(OrderBook ob)
    {

        if (ob.InstrumentId != SourceInstrumentId) return;

        var fairValue = CalculateFairValue(ob);
        if (fairValue is null || fairValue.Value.ToDecimal() == 0m)
        {
            return;
        }

        if (fairValue != _lastFairValue)
        {
            _lastFairValue = fairValue;
            OnFairValueChanged(new FairValueUpdate(SourceInstrumentId, fairValue.Value));
        }
    }
}
