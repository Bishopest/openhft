using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting.Interfaces;

public interface IOrderBookConsumerProvider : IFairValueProvider
{
    void Update(OrderBook ob);
}
