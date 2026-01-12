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
    private readonly IMarketDataManager _marketDataManager;
    private readonly IInstrumentRepository _instrumentRepository;


    /// <summary>
    /// Initializes a new instance of the OrderFactory.
    /// </summary>
    public OrderFactory(
        IOrderRouter orderRouter,
        IOrderGatewayRegistry gatewayRegistry,
        ILogger<Order> orderLogger,
        IClientIdGenerator idGenerator,
        IMarketDataManager marketDataManager,
        IInstrumentRepository instrumentRepository)
    {
        _orderRouter = orderRouter ?? throw new ArgumentNullException(nameof(orderRouter));
        _gatewayRegistry = gatewayRegistry ?? throw new ArgumentNullException(nameof(gatewayRegistry));
        _orderLogger = orderLogger ?? throw new ArgumentNullException(nameof(orderLogger));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _marketDataManager = marketDataManager ?? throw new ArgumentNullException(nameof(marketDataManager));
        _instrumentRepository = instrumentRepository ?? throw new ArgumentNullException(nameof(instrumentRepository));
    }

    /// <summary>
    /// Creates a new IOrder instance with all its dependencies injected.
    /// </summary>
    /// <param name="instrumentId">The ID of the instrument for the order.</param>
    /// <param name="side">The side (Buy/Sell) of the order.</param>
    /// <param name="bookName">The name of the Book fills belongs to</param> 
    /// <returns>A new, initialized IOrder instance.</returns>
    public IOrder Create(int instrumentId, Side side, string bookName, OrderSource source, AlgoOrderType algoType = AlgoOrderType.None)
    {
        // 1. Get the correct Order Gateway for the instrument.
        // This assumes you have a way to know the exchange from the instrumentId.
        // For simplicity, let's assume a method in the registry.
        var gateway = _gatewayRegistry.GetGatewayForInstrument(instrumentId);
        // Use the generator to create the ID.
        long clientId = _idGenerator.NextId(source);
        // 2. Create the new Order instance, injecting its dependencies.
        switch (algoType)
        {
            case AlgoOrderType.OppositeFirst:
                return new OppositeFirstOrder(
                    clientId, instrumentId, side, bookName,
                    _orderRouter, gateway, _orderLogger, _marketDataManager);

            case AlgoOrderType.FirstFollow:
                // FirstFollow는 TickSize를 알기 위해 Instrument 객체가 필요함
                var instrument = _instrumentRepository.GetById(instrumentId);
                return new FirstFollowOrder(
                    clientId, instrument!, side, bookName,
                    _orderRouter, gateway, _orderLogger, _marketDataManager);
            case AlgoOrderType.None:
            default:
                // 일반 주문 생성
                return new Order(
                    clientId, instrumentId, side, bookName,
                    _orderRouter, gateway, _orderLogger);
        }
    }
}