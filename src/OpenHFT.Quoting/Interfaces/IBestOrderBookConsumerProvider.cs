using System;
using OpenHFT.Core.OrderBook;

namespace OpenHFT.Quoting.Interfaces;

public interface IBestOrderBookConsumerProvider : IFairValueProvider
{
    void Update(BestOrderBook bob);
}
