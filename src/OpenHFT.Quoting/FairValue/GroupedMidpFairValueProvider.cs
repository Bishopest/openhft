using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting.FairValue;

public class GroupedMidpFairValueProvider : AbstractFairValueProvider, IOrderBookConsumerProvider
{
    /// <summary>
    /// The multiple of the instrument's tick size used for price grouping.
    /// This is calculated once on the first valid order book update.
    /// </summary>
    private long? _groupingTickMultiple;
    private readonly object _lock = new object();
    private readonly Price _minTick;
    public override FairValueModel Model => FairValueModel.GroupedMidp;

    public GroupedMidpFairValueProvider(ILogger logger, int instrumentId, Price minTick)
        : base(logger, instrumentId)
    {
        _minTick = minTick;
    }

    protected Price? CalculateFairValue(OrderBook orderBook)
    {
        // 1. Get the raw best bid and ask prices.
        var (bestBid, _) = orderBook.GetBestBid();
        var (bestAsk, _) = orderBook.GetBestAsk();

        // Need both sides to calculate anything.
        if (bestBid.ToTicks() == 0 || bestAsk.ToTicks() == 0)
        {
            return null;
        }

        // 2. Lazily initialize the grouping multiple on the first run.
        if (_groupingTickMultiple == null)
        {
            lock (_lock)
            {
                // Double-check lock pattern
                if (_groupingTickMultiple == null)
                {
                    _groupingTickMultiple = CalculateGroupingMultiple(bestBid, bestAsk);
                    Logger.LogInformationWithCaller($"Calculated grouping multiple {_groupingTickMultiple} ticks.");
                }
            }
        }

        // If calculation failed (e.g., tick size is zero), we can't proceed.
        if (_groupingTickMultiple.Value <= 0)
        {
            return null;
        }

        var groupingSizeInTicks = _minTick.ToTicks() * _groupingTickMultiple.Value;

        // 3. Group the prices.
        // Bid price is rounded DOWN to the nearest group boundary.
        var groupedBidTicks = bestBid.ToTicks() / groupingSizeInTicks * groupingSizeInTicks;

        // Ask price is rounded UP to the nearest group boundary.
        // The formula (price + groupSize - 1) / groupSize * groupSize is a standard ceiling integer division trick.
        var groupedAskTicks = (bestAsk.ToTicks() + groupingSizeInTicks - 1) / groupingSizeInTicks * groupingSizeInTicks;

        // 4. Calculate the new mid-price from the grouped prices.
        var groupedMidPrice = Price.FromTicks((groupedBidTicks + groupedAskTicks) / 2);

        return groupedMidPrice;
    }

    /// <summary>
    /// Calculates the optimal multiple 'N' for the tick size to approximate 1 basis point.
    /// </summary>
    private long? CalculateGroupingMultiple(Price currentBid, Price currentAsk)
    {
        // Use the current mid-price as the reference price.
        var currentMidPrice = (currentBid.ToDecimal() + currentAsk.ToDecimal()) / 2m;
        if (currentMidPrice <= 0)
        {
            Logger.LogWarningWithCaller($"Cannot calculate grouping multiple : Current mid-price is zero or negative.");
            return null;
        }

        // 1 basis point (bp) = 0.01% = 0.0001
        var oneBasisPointValue = currentMidPrice * 0.0001m;
        // Calculate N: How many ticks are needed to be closest to 1bp?
        // N = (Value of 1bp in Ticks) / (Value of 1 TickSize in Ticks)
        if (oneBasisPointValue < _minTick.ToDecimal())
        {
            // If 1bp is smaller than a single tick, our smallest possible group is 1 tick.
            return 1;
        }

        // Round to the nearest whole number of ticks.
        var multiple = (long)Math.Round(oneBasisPointValue / _minTick.ToDecimal());

        // Ensure the multiple is at least 1.
        return Math.Max(1, multiple);
    }

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
