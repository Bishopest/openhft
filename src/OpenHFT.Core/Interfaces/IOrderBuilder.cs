using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

/// <summary>
/// Defines a fluent interface for constructing an IOrder object.
/// </summary>
public interface IOrderBuilder
{
    /// <summary>
    /// Sets the price for the order.
    /// </summary>
    IOrderBuilder WithPrice(Price price);

    /// <summary>
    /// Sets the quantity for the order.
    /// </summary>
    IOrderBuilder WithQuantity(Quantity quantity);

    /// <summary>
    /// Sets the order type (e.g., Limit, Market).
    /// </summary>
    IOrderBuilder WithOrderType(OrderType orderType);

    /// <summary>
    /// Sets the Post-Only option for the order.
    /// </summary>
    IOrderBuilder WithPostOnly(bool isPostOnly);

    /// <summary>
    /// ADDED: Registers an event handler for the order's status changes.
    /// This should be called during the build process to ensure the order is fully configured upon creation.
    /// </summary>
    /// <param name="handler">The event handler to subscribe to the StatusChanged event.</param>
    IOrderBuilder WithStatusChangedHandler(EventHandler<OrderStatusReport> handler);

    /// <summary>
    /// Constructs and returns the final IOrder object.
    /// </summary>
    IOrder Build();
}