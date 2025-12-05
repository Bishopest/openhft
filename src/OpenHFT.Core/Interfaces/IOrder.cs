using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

public interface IOrder
{
    public int InstrumentId { get; }

    public string BookName { get; }
    /// <summary>
    /// The unique client-side identifier for this order, assigned upon creation.
    /// </summary>
    long ClientOrderId { get; }

    /// <summary>
    /// The exchange-assigned identifier for this order. May be null until acknowledged by the exchange.
    /// </summary>
    string? ExchangeOrderId { get; }

    /// <summary>
    /// The current status of the order.
    /// </summary>
    OrderStatus Status { get; }

    /// <summary>
    /// The side of the order (Buy or Sell).
    /// </summary>
    Side Side { get; }

    /// <summary>
    /// The price of the order.
    /// </summary>
    Price Price { get; set; }

    /// <summary>
    /// The original quantity of the order.
    /// </summary>
    Quantity Quantity { get; set; }

    /// <summary>
    /// The quantity remaining to be filled.
    /// </summary>
    Quantity LeavesQuantity { get; set; }

    /// <summary>
    /// Gets a value indicating whether this is a Post-Only order.
    /// </summary>
    bool IsPostOnly { get; set; }

    /// <summary>
    /// Gets the type of this order
    /// </summary>
    OrderType OrderType { get; set; }

    /// <summary>
    /// The UTC Unix timestamp of the last update to this order's state.
    /// </summary>
    long LastUpdateTime { get; }

    /// <summary>
    /// The most recent OrderStatusReport received for this order.
    /// </summary>
    OrderStatusReport? LatestReport { get; }

    // --- Feedback Mechanism (Observable) ---

    /// <summary>
    /// Fired whenever the order's status is updated based on a report from the exchange.
    /// The Quoter subscribes to this event to manage its state.
    /// </summary>
    event EventHandler<OrderStatusReport> StatusChanged;

    /// <summary>
    /// Fired whenever a part of the order is filled.
    /// This event provides detailed execution information.
    /// </summary>
    event EventHandler<Fill> OrderFilled;

    /// <summary>
    /// Subscribes a handler to the status changed event.
    /// </summary>
    void AddStatusChangedHandler(EventHandler<OrderStatusReport> handler);

    /// <summary>
    /// Unsubscribes a handler from the status changed event.
    /// Crucial to prevent memory leaks (lapsed listener problem).
    /// </summary>
    void RemoveStatusChangedHandler(EventHandler<OrderStatusReport> handler);

    /// <summary>
    /// Subscribes a handler to the order filled event.
    /// </summary>
    void AddFillHandler(EventHandler<Fill> handler);

    /// <summary>
    /// Unsubscribes a handler from the order filled event.
    /// </summary>
    void RemoveFillHandler(EventHandler<Fill> handler);

    /// <summary>
    /// Clears all internal state and event subscribers.
    /// Essential for GC-Free Object Pooling to reuse this instance safely.
    /// </summary>
    void ResetState();

    // --- Action Methods ---
    /// <summary>
    /// Submits this order to the exchange for the first time.
    /// </summary>
    /// <param name="cancellationToken">A token for cancelling the submission.</param>
    Task SubmitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a request to replace the active order with a new price.
    /// Quantity remains unchanged.(if you want more size, then new order or if you want smaller size, then cancel and new order)
    /// If the exchange does not support atomic replacement, the implementation
    /// should handle the cancel/replace logic internally.
    /// </summary>
    /// <param name="cancellationToken">A token for cancelling the replacement.</param>
    Task ReplaceAsync(Price price, OrderType orderType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a request to cancel the active order.
    /// </summary>
    /// <param name="cancellationToken">A token for cancelling the cancellation request.</param>
    Task CancelAsync(CancellationToken cancellationToken = default);

}
