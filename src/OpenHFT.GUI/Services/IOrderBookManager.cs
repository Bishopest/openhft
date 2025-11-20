using System;
using OpenHFT.Core.OrderBook;

namespace OpenHFT.GUI.Services;

public interface IOrderBookManager
{
    /// <summary>
    /// Event triggered when a new order book snapshot is received.
    /// Components will subscribe to this to get real-time updates.
    /// </summary>
    event EventHandler<OrderBook> OnOrderBookUpdated;
    /// <summary>
    /// Gets the list of currently subscribed symbols.
    /// </summary>
    IEnumerable<int> GetSubscribedIds();
    OrderBook? GetOrderBook(int instrumentId);
}
