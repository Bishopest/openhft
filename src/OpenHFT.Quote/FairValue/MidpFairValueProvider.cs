using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting.FairValue;

/// <summary>
/// A Fair Value Provider that calculates the fair value as the simple mid-price
/// (average of best bid and best ask) from the raw order book data.
/// </summary>
public class MidpFairValueProvider : AbstractFairValueProvider
{
    public override FairValueModel Model => FairValueModel.Midp;

    public MidpFairValueProvider(ILogger logger, int instrumentId) : base(logger, instrumentId)
    {
    }

    protected override Price? CalculateFairValue(OrderBook orderBook)
    {
        var midPrice = orderBook.GetMidPrice();

        if (midPrice.ToDecimal() == 0)
        {
            return null;
        }

        return midPrice;
    }
}
