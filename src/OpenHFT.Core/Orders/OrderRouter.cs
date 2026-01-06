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

    // A queue to hold the IDs of orders pending final deregistration.
    // It acts as a buffer. An order is only truly removed when it's pushed out of this queue.
    private readonly Queue<long> _deregistrationBuffer;
    private readonly int _bufferCapacity;
    // A lock to ensure atomic operations on the non-thread-safe Queue.
    private readonly object _bufferLock = new();
    public event EventHandler<OrderStatusReport> OrderStatusChanged;
    public event EventHandler<Fill> OrderFilled;

    public OrderRouter(ILogger<OrderRouter> logger, int deregistrationBufferSize = 20)
    {
        _logger = logger;
        _bufferCapacity = deregistrationBufferSize > 0 ? deregistrationBufferSize : 1;
        _deregistrationBuffer = new Queue<long>(_bufferCapacity);
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

    /// <summary>
    /// Places the order in a buffer for delayed deregistration.
    /// If the buffer is full, the oldest order in the buffer is dequeued and
    /// removed from the main active orders dictionary.
    /// </summary>
    /// <param name="order">The order to deregister.</param>
    public void DeregisterOrder(IOrder order)
    {
        long orderIdToFinalize = -1;

        // The check, dequeue, and enqueue operations must be atomic.
        lock (_bufferLock)
        {
            // If the buffer is at capacity, make room by removing the oldest item.
            if (_deregistrationBuffer.Count >= _bufferCapacity)
            {
                orderIdToFinalize = _deregistrationBuffer.Dequeue();
            }

            _deregistrationBuffer.Enqueue(order.ClientOrderId);
        }

        // If an order was dequeued, remove it from the active dictionary now.
        // This is done outside the lock to minimize lock contention.
        if (orderIdToFinalize > 0)
        {
            if (_activeOrders.TryRemove(orderIdToFinalize, out var finalOrder))
            {
                _logger.LogDebug("Order {ClientOrderId} was finally deregistered from the router.", orderIdToFinalize);
                if (finalOrder is IOrder ord)
                {
                    ord.ResetState();
                }
            }
        }

        _logger.LogDebug("Order {ClientOrderId} was buffered for lazy deregistration.", order.ClientOrderId);
    }

    public void RouteReport(in OrderStatusReport report)
    {
        if (_activeOrders.TryGetValue(report.ClientOrderId, out var updatableOrder))
        {
            try
            {
                // Forward the report to the correct order instance.
                updatableOrder.OnStatusReportReceived(report);
                if (report.Status == OrderStatus.Filled)
                {
                    _logger.LogInformationWithCaller($"Order {report.ClientOrderId} reached terminal state: {report.Status}. ExchangeID: {report.ExchangeOrderId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCaller(ex, $"An error occurred while processing a status report for ClientOrderId {report.ClientOrderId}.");
            }
        }
        else
        {
            _logger.LogWarningWithCaller($"Received OrderStatusReport for unknown ClientOrderId: {report.ClientOrderId}. " +
                $"Status: {report.Status}, ExchangeID: {report.ExchangeOrderId}, Price: {report.Price.ToDecimal()}");
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