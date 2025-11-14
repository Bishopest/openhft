using System;
using System.Collections.Concurrent;
using System.Text.Json;
using OpenHFT.Core.Models;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

public class OrderCacheService : IOrderCacheService, IDisposable
{
    private readonly IOmsConnectorService _connector;
    private readonly JsonSerializerOptions _jsonOptions;

    // --- KEY CHANGE: Outer key is OmsIdentifier, inner key is InstrumentId ---
    // { "OMS_A": { 1001: { ...orders... } } }
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, ConcurrentDictionary<long, OrderStatusReport>>> _activeOrdersByOms = new();

    // Fills are still stored in a single queue, but the Fill object itself should contain OmsIdentifier.
    private readonly ConcurrentQueue<Fill> _fills = new();
    private const int MaxFillsToStore = 200;

    public event Action<(string OmsIdentifier, OrderStatusReport Report)>? OnOrderUpdated;
    public event Action<(string OmsIdentifier, Fill Fill)>? OnFillReceived;

    public OrderCacheService(IOmsConnectorService connector, JsonSerializerOptions jsonOptions)
    {
        _jsonOptions = jsonOptions;
        _connector = connector;
        _connector.OnMessageReceived += HandleRawMessage;
    }

    private void HandleRawMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();

        switch (type)
        {
            case "ACTIVE_ORDERS_LIST":
                var activeOrdersListEvent = JsonSerializer.Deserialize<ActiveOrdersListEvent>(json, _jsonOptions);
                if (activeOrdersListEvent != null) HandleActiveOrdersList(activeOrdersListEvent.Payload);
                break;
            case "FILLS_LIST":
                var fillEvent = JsonSerializer.Deserialize<FillsListEvent>(json, _jsonOptions);
                if (fillEvent != null) HandleOrderFill(fillEvent.Payload);
                break;
            default:
                break;
        }
    }

    private void HandleActiveOrdersList(ActiveOrdersPayload payload)
    {
        // Get or create the dictionary for this specific OMS
        var omsOrders = _activeOrdersByOms.GetOrAdd(payload.OmsIdentifier, new ConcurrentDictionary<int, ConcurrentDictionary<long, OrderStatusReport>>());

        foreach (var report in payload.Reports)
        {
            var instrumentOrders = omsOrders.GetOrAdd(report.InstrumentId, new ConcurrentDictionary<long, OrderStatusReport>());
            var isActive = IsActiveOrder(report);
            if (isActive)
            {
                instrumentOrders[report.ClientOrderId] = report;
            }
            else
            {
                instrumentOrders.Remove(report.ClientOrderId, out var osr);
            }
        }
        // Notify for each updated order
        foreach (var report in payload.Reports)
        {
            OnOrderUpdated?.Invoke((payload.OmsIdentifier, report));
        }
    }

    private void HandleOrderFill(FillsPayload payload)
    {
        // It's crucial that the Fill object itself can store the OmsIdentifier.
        // Let's assume the Fill class/struct has an OmsIdentifier property.
        var fills = payload.Fills;
        // fill.OmsIdentifier = payload.OmsIdentifier; // Assign if possible

        foreach (var fill in fills)
        {
            _fills.Enqueue(fill);
            while (_fills.Count > MaxFillsToStore) _fills.TryDequeue(out _);
            OnFillReceived?.Invoke((payload.OmsIdentifier, fill));
        }
    }


    public IEnumerable<OrderStatusReport> GetActiveOrders(string omsIdentifier, int instrumentId)
    {
        if (_activeOrdersByOms.TryGetValue(omsIdentifier, out var omsOrders) &&
            omsOrders.TryGetValue(instrumentId, out var instrumentOrders))
        {
            return instrumentOrders.Values.OrderByDescending(o => o.Timestamp);
        }
        return Enumerable.Empty<OrderStatusReport>();
    }

    public IEnumerable<Fill> GetAllFills()
    {
        return _fills.OrderByDescending(f => f.Timestamp);
    }

    private bool IsActiveOrder(OrderStatusReport report)
    {
        var status = report.Status;

        switch (status)
        {
            case OrderStatus.Cancelled:
            case OrderStatus.Rejected:
            case OrderStatus.Filled:
                return false;
            default:
                return true;
        }
    }

    public void Dispose()
    {
        _connector.OnMessageReceived -= HandleRawMessage;
    }

}
