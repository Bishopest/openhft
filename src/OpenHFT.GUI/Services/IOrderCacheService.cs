using System;
using OpenHFT.Core.Models;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

public interface IOrderCacheService
{
    /// <summary>
    /// Fired when an order status is updated. The tuple contains the OMS identifier and the report.
    /// </summary>
    event Action<(string OmsIdentifier, OrderStatusReport Report)>? OnOrderUpdated;

    /// <summary>
    /// Fired when a new fill is received. The tuple contains the OMS identifier and the fill data.
    /// </summary>
    event Action<(string OmsIdentifier, Fill Fill)>? OnFillReceived;

    event Action OnInstancesUpdated;

    /// <summary>
    /// Gets all active orders for a specific instrument on a specific OMS.
    /// </summary>
    IEnumerable<OrderStatusReport> GetActiveOrders(string omsIdentifier, int instrumentId);
    public string? GetOmsIdentifierForOrder(string exchangeOrderId);
    /// <summary>
    /// Gets all recent fills from all connected OMS servers.
    /// </summary>
    IEnumerable<Fill> GetAllFills();
    IEnumerable<InstanceStatusPayload> GetAllActiveInstances();
}
