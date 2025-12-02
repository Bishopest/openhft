using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Orders;

/// <summary>
/// A concrete implementation of the IOrderBuilder that uses an IOrderFactory
/// to create and configure an IOrder instance.
/// </summary>
public class OrderBuilder : IOrderBuilder
{
    private readonly IOrderSettable _settleableOrder;

    /// <summary>
    /// Initializes a new instance of the OrderBuilder.
    /// It receives a pre-created (potentially pooled) IOrder object from a factory.
    /// </summary>
    /// <param name="orderFactory">The factory to create the base order object.</param>
    /// <param name="instrumentId">The ID of the instrument for the order.</param>
    /// <param name="side">The side (Buy/Sell) for the order.</param>
    /// <param name="bookName">The name of the Book fills belongs to</param> 
    public OrderBuilder(IOrderFactory orderFactory, int instrumentId, Side side, string bookName)
    {
        if (orderFactory == null) throw new ArgumentNullException(nameof(orderFactory));

        // The factory provides the basic shell of the order.
        IOrder baseOrder = orderFactory.Create(instrumentId, side, bookName);

        // Factory가 반환한 객체는 반드시 IOrderSettable이어야 합니다.
        _settleableOrder = baseOrder as IOrderSettable
                           ?? throw new InvalidCastException("Factory must create an object implementing IOrderSettable.");
    }

    public IOrderBuilder WithPrice(Price price)
    {
        _settleableOrder.Price = price;
        return this;
    }

    public IOrderBuilder WithQuantity(Quantity quantity)
    {
        _settleableOrder.Quantity = quantity;
        _settleableOrder.LeavesQuantity = quantity;
        return this;
    }

    public IOrderBuilder WithOrderType(OrderType orderType)
    {
        _settleableOrder.OrderType = orderType;
        return this;
    }

    public IOrderBuilder WithStatusChangedHandler(EventHandler<OrderStatusReport> handler)
    {
        _settleableOrder.AddStatusChangedHandler(handler);
        return this;
    }

    public IOrderBuilder WithPostOnly(bool isPostOnly)
    {
        _settleableOrder.IsPostOnly = isPostOnly; // Order 클래스에 IsPostOnly 속성 추가 필요
        return this;
    }

    public IOrder Build()
    {
        // Perform any final validation before returning the order.
        var order = (IOrder)_settleableOrder;
        if (order.Price.ToTicks() <= 0 || order.Quantity.ToTicks() <= 0)
        {
            throw new InvalidOperationException("Order price and quantity must be positive.");
        }

        return order;
    }
}