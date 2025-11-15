using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Core.Orders;

/// <summary>
/// A thread-safe implementation of IOrderRouter that uses a ConcurrentDictionary
/// to map ClientOrderIds to active IOrder instances.
/// </summary>
public class OrderRouter : IOrderRouter
{
    private readonly ILogger<OrderRouter> _logger;
    private readonly ConcurrentDictionary<long, IOrderUpdatable> _activeOrders = new();

    public event EventHandler<OrderStatusReport> OrderStatusChanged;
    public event EventHandler<Fill> OrderFilled;

    public OrderRouter(ILogger<OrderRouter> logger)
    {
        _logger = logger;
    }

    public IReadOnlyCollection<IOrder> GetActiveOrders()
    {
        return _activeOrders.Values.Cast<IOrder>().ToList().AsReadOnly();
    }


    public void RegisterOrder(IOrder order)
    {
        if (order is not IOrderUpdatable updatableOrder)
        {
            _logger.LogWarningWithCaller($"Order with ClientOrderId {order.ClientOrderId} does not implement IOrderUpdatable and cannot be registered.");
            return;
        }

        if (_activeOrders.TryAdd(order.ClientOrderId, updatableOrder))
        {
            _logger.LogDebug("Order {ClientOrderId} registered with the router.", order.ClientOrderId);
        }
        else
        {
            _logger.LogWarningWithCaller($"Attempted to register an order with a duplicate ClientOrderId {order.ClientOrderId}.");
        }
    }

    public void DeregisterOrder(IOrder order)
    {
        if (_activeOrders.TryRemove(order.ClientOrderId, out _))
        {
            _logger.LogDebug("Order {ClientOrderId} deregistered from the router.", order.ClientOrderId);
        }
    }

    public void RouteReport(in OrderStatusReport report)
    {
        if (_activeOrders.TryGetValue(report.ClientOrderId, out var updatableOrder))
        {
            try
            {
                // Forward the report to the correct order instance.
                updatableOrder.OnStatusReportReceived(report);
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCaller(ex, $"An error occurred while processing a status report for ClientOrderId {report.ClientOrderId}.");
            }
        }
    }

    public void RaiseStatusChanged(IOrder order, OrderStatusReport report)
    {
        OrderStatusChanged?.Invoke(order, report);
    }

    public void RaiseOrderFilled(IOrder order, Fill fill)
    {
        OrderFilled?.Invoke(order, fill);
    }
}