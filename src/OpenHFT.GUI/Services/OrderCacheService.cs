using System;
using System.Collections.Concurrent;
using OpenHFT.Core.Models;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

public class OrderCacheService : IOrderCacheService, IDisposable
{
    private readonly IOmsConnectorService _connector;
    // Key: InstrumentId, Value: Dictionary of active orders (Key: ClientOrderId)
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, OrderStatusReport>> _activeOrders = new();
    private readonly ConcurrentQueue<Fill> _fills = new();

    public event EventHandler<OrderStatusReport>? OnOrdersUpdated;
    public event EventHandler<Fill>? OnFillsUpdated;

    public OrderCacheService(IOmsConnectorService connector)
    {
        _connector = connector;
        _connector.OnActiveOrderListReceived += HandleOrderStatusUpdate;
        _connector.OnFillsListReceived += HandleOrderFill;
    }

    private void HandleOrderStatusUpdate(ActiveOrdersListEvent e)
    {
        var reports = e.Reports;

        foreach (var report in reports)
        {
            var ordersForInstrument = _activeOrders.GetOrAdd(report.InstrumentId, new ConcurrentDictionary<long, OrderStatusReport>());

            if (report.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected)
            {
                ordersForInstrument.TryRemove(report.ClientOrderId, out _);
            }
            else
            {
                ordersForInstrument[report.ClientOrderId] = report;
            }
            OnOrdersUpdated?.Invoke(this, report);
        }
    }

    private void HandleOrderFill(FillsListEvent e)
    {
        foreach (var fill in e.Fills)
        {
            _fills.Enqueue(fill);
            // Optional: Limit the size of the fills queue
            while (_fills.Count > 100) _fills.TryDequeue(out _);
            OnFillsUpdated?.Invoke(this, fill);
        }
    }

    public IEnumerable<OrderStatusReport> GetActiveOrders(int instrumentId)
    {
        return _activeOrders.TryGetValue(instrumentId, out var orders)
            ? orders.Values.OrderByDescending(o => o.Timestamp)
            : Enumerable.Empty<OrderStatusReport>();
    }

    public IEnumerable<Fill> GetAllFills()
    {
        return _fills.OrderByDescending(f => f.Timestamp);
    }

    public void Dispose()
    {
        _connector.OnActiveOrderListReceived -= HandleOrderStatusUpdate;
        _connector.OnFillsListReceived -= HandleOrderFill;
    }
}
