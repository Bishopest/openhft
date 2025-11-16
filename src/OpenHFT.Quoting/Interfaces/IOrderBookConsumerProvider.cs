using System;
using OpenHFT.Book.Core;

namespace OpenHFT.Quoting.Interfaces;

public interface IOrderBookConsumerProvider : IFairValueProvider
{
    void Update(OrderBook ob);
}
