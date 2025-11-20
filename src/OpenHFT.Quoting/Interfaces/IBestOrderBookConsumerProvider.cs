using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting.Interfaces;

public interface IBestOrderBookConsumerProvider : IFairValueProvider
{
    void Update(BestOrderBook bob);
}
