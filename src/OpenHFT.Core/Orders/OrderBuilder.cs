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
    public OrderBuilder(IOrderFactory orderFactory, int instrumentId, Side side, string bookName)
    {
        if (orderFactory == null) throw new ArgumentNullException(nameof(orderFactory));

        // The factory provides the basic shell of the order.
        _order = orderFactory.Create(instrumentId, side, bookName);
    }

    public IOrderBuilder WithBookName(string bookName)
    {
        if (_order is Order concreteOrder)
        {
            concreteOrder.BookName = bookName;
        }
        return this;
    }

    public IOrderBuilder WithPrice(Price price)
    {
        // In a real implementation, you would set a property on the concrete Order class.
        // This requires casting or a mutable interface, which can be tricky.
        // Let's assume the concrete Order class has mutable properties for the builder.
        if (_order is Order concreteOrder)
        {
            concreteOrder.Price = price;
        }
        return this;
    }

    public IOrderBuilder WithQuantity(Quantity quantity)
    {
        if (_order is Order concreteOrder)
        {
            concreteOrder.Quantity = quantity;
            // When first built, remaining quantity is the same as the initial quantity.
            concreteOrder.LeavesQuantity = quantity;
        }
        return this;
    }

    public IOrderBuilder WithOrderType(OrderType orderType)
    {
        if (_order is Order concreteOrder)
        {
            concreteOrder.OrderType = orderType;
        }
        return this;
    }

    public IOrderBuilder WithStatusChangedHandler(EventHandler<OrderStatusReport> handler)
    {
        if (handler != null)
        {
            _order.StatusChanged += handler;
        }
        return this;
    }

    public IOrderBuilder WithPostOnly(bool isPostOnly)
    {
        if (_order is Order concreteOrder)
        {
            concreteOrder.IsPostOnly = isPostOnly; // Order 클래스에 IsPostOnly 속성 추가 필요
        }
        return this;
    }

    public IOrder Build()
    {
        // Perform any final validation before returning the order.
        if (_order.Price.ToTicks() <= 0 || _order.Quantity.ToTicks() <= 0)
        {
            throw new InvalidOperationException("Order price and quantity must be positive.");
        }

        return _order;
    }
}