using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Orders;


/// <summary>
/// A request to place a new order.
/// </summary>
public readonly struct NewOrderRequest
{
    public readonly int InstrumentId { get; }
    public readonly long ClientOrderId { get; }
    public readonly Side Side { get; }
    public readonly Price Price { get; }
    public readonly Quantity Quantity { get; }
    public readonly OrderType OrderType { get; }
    // Add other fields like TimeInForce, PostOnly, etc. as needed.

    public NewOrderRequest(int instrumentId, long clientOrderId, Side side, Price price, Quantity quantity, OrderType orderType)
    {
        InstrumentId = instrumentId;
        ClientOrderId = clientOrderId;
        Side = side;
        Price = price;
        Quantity = quantity;
        OrderType = orderType;
    }
}

/// <summary>
/// A request to replace an existing order.
/// </summary>
public readonly struct ReplaceOrderRequest
{
    public readonly string OrderId { get; }
    public readonly Price NewPrice { get; }

    public ReplaceOrderRequest(string orderId, Price newPrice)
    {
        OrderId = orderId;
        NewPrice = newPrice;
    }
}

/// <summary>
/// A request to cancel an existing order.
/// </summary>
public readonly struct CancelOrderRequest
{
    public readonly string OrderId { get; }

    public CancelOrderRequest(string orderId)
    {
        OrderId = orderId;
    }
}