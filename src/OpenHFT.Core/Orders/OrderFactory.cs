using System;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<Order> _orderLogger;
    private readonly IClientIdGenerator _idGenerator;


    /// <summary>
    /// Initializes a new instance of the OrderFactory.
    /// </summary>
    public OrderFactory(IOrderRouter orderRouter, IOrderGatewayRegistry gatewayRegistry, ILogger<Order> orderLogger, IClientIdGenerator idGenerator)
    {
        _orderRouter = orderRouter ?? throw new ArgumentNullException(nameof(orderRouter));
        _gatewayRegistry = gatewayRegistry ?? throw new ArgumentNullException(nameof(gatewayRegistry));
        _orderLogger = orderLogger ?? throw new ArgumentNullException(nameof(orderLogger));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
    }

    /// <summary>
    /// Creates a new IOrder instance with all its dependencies injected.
    /// </summary>
    /// <param name="instrumentId">The ID of the instrument for the order.</param>
    /// <param name="side">The side (Buy/Sell) of the order.</param>
    /// <param name="bookName">The name of the Book fills belongs to</param> 
    /// <returns>A new, initialized IOrder instance.</returns>
    public IOrder Create(int instrumentId, Side side, string bookName, OrderSource source)
    {
        // 1. Get the correct Order Gateway for the instrument.
        // This assumes you have a way to know the exchange from the instrumentId.
        // For simplicity, let's assume a method in the registry.
        var orderGateway = _gatewayRegistry.GetGatewayForInstrument(instrumentId);
        // Use the generator to create the ID.
        long clientOrderId = _idGenerator.NextId(source);
        // 2. Create the new Order instance, injecting its dependencies.
        var order = new Order(
            clientOrderId,
            instrumentId,
            side,
            bookName,
            _orderRouter,
            orderGateway ?? throw new ArgumentNullException("orderGateway"),
            _orderLogger
        );

        return order;
    }
}