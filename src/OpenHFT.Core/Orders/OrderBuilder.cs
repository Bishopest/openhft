using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Orders;

/// <summary>
/// A concrete implementation of the IOrderBuilder that uses an IOrderFactory
/// to create and configure an IOrder instance.
/// </summary>
public class OrderBuilder : IOrderBuilder
{
    private readonly IOrder _order;

    /// <summary>
    /// Initializes a new instance of the OrderBuilder.
    /// It receives a pre-created (potentially pooled) IOrder object from a factory.
    /// </summary>
    /// <param name="orderFactory">The factory to create the base order object.</param>
    /// <param name="instrumentId">The ID of the instrument for the order.</param>
    /// <param name="side">The side (Buy/Sell) for the order.</param>
    /// <param name="bookName">The name of the Book fills belongs to</param> 
    public OrderBuilder(IOrderFactory orderFactory, int instrumentId, Side side, string bookName, OrderSource orderSource, AlgoOrderType algoType = AlgoOrderType.None)
    {
        if (orderFactory == null) throw new ArgumentNullException(nameof(orderFactory));

        IOrder baseOrder = orderFactory.Create(instrumentId, side, bookName, orderSource, algoType);
        _order = baseOrder;
    }

    public IOrderBuilder WithPrice(Price price)
    {
        _order.Price = price;
        return this;
    }

    public IOrderBuilder WithQuantity(Quantity quantity)
    {
        _order.Quantity = quantity;
        _order.LeavesQuantity = quantity;
        return this;
    }

    public IOrderBuilder WithOrderType(OrderType orderType)
    {
        _order.OrderType = orderType;
        return this;
    }

    public IOrderBuilder WithStatusChangedHandler(EventHandler<OrderStatusReport> handler)
    {
        _order.AddStatusChangedHandler(handler);
        return this;
    }

    public IOrderBuilder WithFillHandler(EventHandler<Fill> handler)
    {
        _order.AddFillHandler(handler);
        return this;
    }

    public IOrderBuilder WithPostOnly(bool isPostOnly)
    {
        _order.IsPostOnly = isPostOnly; // Order 클래스에 IsPostOnly 속성 추가 필요
        return this;
    }

    public IOrder Build()
    {
        // Perform any final validation before returning the order.
        var order = (IOrder)_order;

        bool isAlgo = order.AlgoOrderType != AlgoOrderType.None;

        // AlgoOrder가 아닌 경우에만 가격 > 0 필수 체크
        if (!isAlgo && order.Price.ToTicks() <= 0)
        {
            throw new InvalidOperationException("Standard Order price must be positive.");
        }

        // 수량은 필수
        if (order.Quantity.ToTicks() <= 0)
        {
            throw new InvalidOperationException("Order quantity must be positive.");
        }

        return order;
    }
}