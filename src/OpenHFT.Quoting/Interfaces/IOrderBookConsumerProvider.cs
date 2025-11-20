using System;
using OpenHFT.Core.OrderBook;

namespace OpenHFT.Quoting.Interfaces;

public interface IOrderBookConsumerProvider : IFairValueProvider
{
    void Update(OrderBook ob);
}
