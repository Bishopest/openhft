using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

public interface IAlgoOrder : IOrder
{
    void OnMarketDataUpdated(OrderBook book);
    void StopAlgo();
}