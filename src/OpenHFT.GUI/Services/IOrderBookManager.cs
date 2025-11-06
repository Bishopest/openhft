using System;
using OpenHFT.Book.Core;

namespace OpenHFT.GUI.Services;

public interface IOrderBookManager
{
    /// <summary>
    /// Event triggered when a new order book snapshot is received.
    /// Components will subscribe to this to get real-time updates.
    /// </summary>
    event Action<OrderBook> OnOrderBookUpdated;

    /// <summary>
    /// Connects to an OMS and subscribes to the necessary instrument order books.
    /// </summary>
    /// <param name="instrumentId">The ID of the instrument to subscribe.</param>
    Task ConnectAndSubscribeAsync(int instrumentId);

    /// <summary>
    /// Disconnects and clears all subscriptions.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Gets the list of currently subscribed symbols.
    /// </summary>
    IEnumerable<int> GetSubscribedIds();
}
