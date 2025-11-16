using System;
using OpenHFT.Book.Core;

namespace OpenHFT.Quoting.Interfaces;

public interface IBestOrderBookConsumerProvider : IFairValueProvider
{
    void Update(BestOrderBook bob);
}
