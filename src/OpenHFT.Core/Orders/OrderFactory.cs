using System;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Orders;

/// <summary>
/// A concrete factory for creating IOrder instances.
/// It resolves necessary dependencies from the DI container to construct a fully functional Order object.
/// </summary>
public class OrderFactory : IOrderFactory
{
    private readonly IOrderRouter _orderRouter;
    private readonly IOrderGatewayRegistry _gatewayRegistry;

    /// <summary>
    /// Initializes a new instance of the OrderFactory.
    /// </summary>
    /// <param name="orderRouter">The central router for order status updates.</param>
    /// <param name="gatewayRegistry">The registry to find the correct order gateway for an instrument.</param>
    public OrderFactory(IOrderRouter orderRouter, IOrderGatewayRegistry gatewayRegistry)
    {
        _orderRouter = orderRouter ?? throw new ArgumentNullException(nameof(orderRouter));
        _gatewayRegistry = gatewayRegistry ?? throw new ArgumentNullException(nameof(gatewayRegistry));
    }

    /// <summary>
    /// Creates a new IOrder instance with all its dependencies injected.
    /// </summary>
    /// <param name="instrumentId">The ID of the instrument for the order.</param>
    /// <param name="side">The side (Buy/Sell) of the order.</param>
    /// <returns>A new, initialized IOrder instance.</returns>
    public IOrder Create(int instrumentId, Side side)
    {
        // 1. Get the correct Order Gateway for the instrument.
        // This assumes you have a way to know the exchange from the instrumentId.
        // For simplicity, let's assume a method in the registry.
        var orderGateway = _gatewayRegistry.GetGatewayForInstrument(instrumentId);

        // 2. Create the new Order instance, injecting its dependencies.
        var order = new Order(
            instrumentId,
            side,
            _orderRouter,
            orderGateway
        );

        return order;
    }
}