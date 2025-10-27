using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

/// <summary>
/// Defines an internal contract for an order that can be updated by the routing system.
/// This separates the public-facing actions (IOrder) from the internal feedback mechanism.
/// </summary>
public interface IOrderUpdatable
{
    /// <summary>
    /// Called by the OrderRouter when an OrderStatusReport for this order is received.
    /// </summary>
    /// <param name="report">The status report from the exchange.</param>
    void OnStatusReportReceived(in OrderStatusReport report);
}