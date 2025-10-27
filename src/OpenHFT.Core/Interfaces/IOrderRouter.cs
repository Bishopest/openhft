using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

/// <summary>
/// Defines the contract for a router that dispatches OrderStatusReports to the correct IOrder instances.
/// </summary>
public interface IOrderRouter
{
    /// <summary>
    /// Registers an order to begin receiving status reports.
    /// This is typically called by the IOrder instance itself upon creation.
    /// </summary>
    /// <param name="order">The order to register.</param>
    void RegisterOrder(IOrder order);

    /// <summary>
    /// Deregisters an order to stop receiving status reports.
    /// This is typically called by the IOrder instance when it reaches a terminal state.
    /// </summary>
    /// <param name="order">The order to deregister.</param>
    void DeregisterOrder(IOrder order);

    /// <summary>
    /// Routes an incoming status report to the appropriate registered order.
    /// This is called by the disruptor consumer (e.g., OrderUpdateDistributor).
    /// </summary>
    /// <param name="report">The report to be routed.</param>
    void RouteReport(in OrderStatusReport report);
}