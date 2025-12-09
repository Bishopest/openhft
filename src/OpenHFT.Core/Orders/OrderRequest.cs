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

    public override string ToString() =>
        $"NEW | CID:{ClientOrderId} | {Side} {Quantity.ToDecimal()} @ {Price.ToDecimal()} " +
        $"| ID:{InstrumentId} | Type:{OrderType} | PostOnly:{IsPostOnly}";
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

    public override string ToString() =>
        $"REPLACE | EXID:{OrderId} | New Price: {NewPrice.ToDecimal()} | ID:{InstrumentId}";
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

    public override string ToString() =>
        $"CANCEL | EXID:{OrderId} | ID:{InstrumentId}";
}

/// <summary>
/// A request to cancel multiple existing orders in a single batch.
/// </summary>
public readonly struct BulkCancelOrdersRequest
{
    // BitMEX supports cancelling by either ID, so we can include both.
    public readonly IReadOnlyList<string> ExchangeOrderIds { get; }
    // public readonly IReadOnlyList<long> ClientOrderIds { get; } // For exchanges supporting it
    public readonly int InstrumentId { get; }

    public BulkCancelOrdersRequest(IReadOnlyList<string> exchangeOrderIds, int instrumentId)
    {
        ExchangeOrderIds = exchangeOrderIds;
        InstrumentId = instrumentId;
    }

    public override string ToString()
    {
        // 취소 ID가 많을 경우를 대비하여 첫 3개만 표시하고 나머지는 생략합니다.
        string idSummary = ExchangeOrderIds.Count > 3
            ? string.Join(", ", ExchangeOrderIds.Take(3)) + $", ...({ExchangeOrderIds.Count - 3} more)"
            : string.Join(", ", ExchangeOrderIds);

        return $"BULK CANCEL | Count:{ExchangeOrderIds.Count} | IDs: [{idSummary}] | ID:{InstrumentId}";
    }
}