using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting.FairValue;

public class OrderBookImbalanceFairValueProvider : AbstractFairValueProvider, IOrderBookConsumerProvider
{
    private readonly decimal _groupingBps;

    public override FairValueModel Model => FairValueModel.OrderBookImbalance;

    public OrderBookImbalanceFairValueProvider(ILogger logger, int instrumentId, decimal groupingBps)
        : base(logger, instrumentId)
    {
        _groupingBps = groupingBps;
    }

    /// <summary>
    /// This is the entry point called by the MarketDataManager when a new order book update arrives.
    /// </summary>
    public void Update(OrderBook orderBook)
    {
        if (orderBook.InstrumentId != SourceInstrumentId) return;

        var fairValue = CalculateImbalanceFairValue(orderBook);
        if (fairValue is null || fairValue.Value.ToDecimal() == 0m)
        {
            return;
        }

        // Since this model produces a single fair value, we check if it has changed
        // and update both bid/ask fair values with the same new value.
        if (fairValue.Value != _lastFairAskValue || fairValue.Value != _lastFairBidValue)
        {
            _lastFairAskValue = fairValue;
            _lastFairBidValue = fairValue;
            OnFairValueChanged(new FairValueUpdate(SourceInstrumentId, fairValue.Value, fairValue.Value));
        }
    }

    /// <summary>
    /// Implements the core logic for calculating the fair value based on order book imbalance.
    /// </summary>
    /// <param name="orderBook">The current snapshot of the order book.</param>
    /// <returns>The calculated fair value, or null if it cannot be determined.</returns>
    private Price? CalculateImbalanceFairValue(OrderBook orderBook)
    {
        // 1. Get best bid and ask to determine the current market spread.
        var (bestBidPrice, _) = orderBook.GetBestBid();
        var (bestAskPrice, _) = orderBook.GetBestAsk();

        if (bestBidPrice.ToTicks() <= 0 || bestAskPrice.ToTicks() <= 0)
        {
            // Not enough data to calculate a fair value.
            return null;
        }

        // 2. Calculate mid-price and the price boundaries for imbalance calculation.
        var midPriceDecimal = (bestBidPrice.ToDecimal() + bestAskPrice.ToDecimal()) / 2.0m;
        var groupingFactor = _groupingBps * 0.0001m; // 1 basis point = 0.0001
        var lowerBound = midPriceDecimal * (1 - groupingFactor);
        var upperBound = midPriceDecimal * (1 + groupingFactor);

        // 3. Calculate cumulative sizes within the price boundaries.
        // We use decimal for precision in financial calculations.
        decimal totalBidSizeInRange = 0m;
        decimal totalAskSizeInRange = 0m;

        // Sum bid sizes within the lower bound.
        // OrderBook.Bids is sorted best (highest price) to worst.
        foreach (var level in orderBook.Bids)
        {
            if (level.Price.ToDecimal() >= lowerBound)
            {
                totalBidSizeInRange += level.TotalQuantity.ToDecimal();
            }
            else
            {
                // Since bids are sorted descending, we can break early.
                break;
            }
        }

        // Sum ask sizes within the upper bound.
        // OrderBook.Asks is sorted best (lowest price) to worst.
        foreach (var level in orderBook.Asks)
        {
            if (level.Price.ToDecimal() <= upperBound)
            {
                totalAskSizeInRange += level.TotalQuantity.ToDecimal();
            }
            else
            {
                // Since asks are sorted ascending, we can break early.
                break;
            }
        }

        // 4. Calculate the Order Book Imbalance (OBI) ratio.
        var totalVolume = totalBidSizeInRange + totalAskSizeInRange;
        if (totalVolume == 0m)
        {
            // If there's no volume in the range, the fair price is the mid-price.
            return Price.FromDecimal(midPriceDecimal);
        }

        var imbalanceRatio = totalBidSizeInRange / totalVolume;

        // 5. Calculate the imbalance-adjusted fair price.
        // This is a weighted average of the best bid and ask prices.
        var fairPriceDecimal = (bestAskPrice.ToDecimal() * imbalanceRatio) +
                               (bestBidPrice.ToDecimal() * (1.0m - imbalanceRatio));

        return Price.FromDecimal(fairPriceDecimal);
    }
}
