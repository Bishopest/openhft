using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

public interface IMarketDataManager
{
    OrderBook? GetOrderBook(int instrumentId);
    BestOrderBook? GetBestOrderBook(int instrumentId);
    void SubscribeOrderBook(int instrumentId, string subscriberName, EventHandler<OrderBook> callback);
    void UnsubscribeOrderBook(int instrumentId, string subscriberName);
    void SubscribeBestOrderBook(int instrumentId, string subscriberName, EventHandler<BestOrderBook> callback);
    void UnsubscribeBestOrderBook(int instrumentId, string subscriberName);
}
