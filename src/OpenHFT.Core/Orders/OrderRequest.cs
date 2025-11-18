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
    public readonly bool IsPostOnly { get; }
    // Add other fields like TimeInForce, PostOnly, etc. as needed.

    public NewOrderRequest(int instrumentId, long clientOrderId, Side side, Price price, Quantity quantity, OrderType orderType, bool isPostOnly)
    {
        InstrumentId = instrumentId;
        ClientOrderId = clientOrderId;
        Side = side;
        Price = price;
        Quantity = quantity;
        OrderType = orderType;
        IsPostOnly = isPostOnly;
    }
}

/// <summary>
/// A request to replace an existing order.
/// </summary>
public readonly struct ReplaceOrderRequest
{
    public readonly string OrderId { get; }
    public readonly Price NewPrice { get; }
    public readonly int InstrumentId { get; }

    public ReplaceOrderRequest(string orderId, Price newPrice, int instrumentId)
    {
        OrderId = orderId;
        NewPrice = newPrice;
        InstrumentId = instrumentId;
    }
}

/// <summary>
/// A request to cancel an existing order.
/// </summary>
public readonly struct CancelOrderRequest
{
    public readonly string OrderId { get; }
    public readonly int InstrumentId { get; }

    public CancelOrderRequest(string orderId, int instrumentId)
    {
        OrderId = orderId;
        InstrumentId = instrumentId;
    }
}