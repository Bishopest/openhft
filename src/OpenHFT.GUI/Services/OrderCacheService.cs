using System;
using System.Collections.Concurrent;
using System.Text.Json;
using OpenHFT.Core.Configuration;
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
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, InstanceStatusPayload>> _activeInstancesByOms = new();
    public event Action? OnInstancesUpdated;

    // Fills are still stored in a single queue, but the Fill object itself should contain OmsIdentifier.
    private readonly ConcurrentDictionary<string, Fill> _fillsByExecutionId = new();
    // Use a queue to maintain insertion order and easily remove the oldest.
    private readonly ConcurrentQueue<string> _fillExecutionIdQueue = new();
    private const int MaxFillsToStore = 200;

    public event Action<(string OmsIdentifier, OrderStatusReport Report)>? OnOrderUpdated;
    public event Action<(string OmsIdentifier, Fill Fill)>? OnFillReceived;

    public OrderCacheService(IOmsConnectorService connector, JsonSerializerOptions jsonOptions)
    {
        _jsonOptions = jsonOptions;
        _connector = connector;
        _connector.OnMessageReceived += HandleRawMessage;
        _connector.OnConnectionStatusChanged += HandleConnectionStatusChange;
    }

    /// <summary>
    /// Handles connection status changes to clear caches for disconnected servers.
    /// </summary>
    private void HandleConnectionStatusChange((OmsServerConfig Server, ConnectionStatus Status) args)
    {
        // We only care about disconnections or errors.
        if (args.Status == ConnectionStatus.Disconnected || args.Status == ConnectionStatus.Error)
        {
            var omsId = args.Server.OmsIdentifier;

            // Try to remove the entire dictionary of orders for the disconnected OMS.
            _activeOrdersByOms.TryRemove(omsId, out var removedOrders);
            _activeInstancesByOms.TryRemove(omsId, out var removedInstances);
            OnInstancesUpdated?.Invoke();
        }
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
            case "INSTANCE_STATUS":
                var statusEvent = JsonSerializer.Deserialize<InstanceStatusEvent>(json, _jsonOptions);
                if (statusEvent != null) HandleInstanceStatusUpdate(statusEvent.Payload);
                break;
            default:
                break;
        }
    }

    private void HandleInstanceStatusUpdate(InstanceStatusPayload payload)
    {
        var omsInstances = _activeInstancesByOms.GetOrAdd(payload.OmsIdentifier, new ConcurrentDictionary<int, InstanceStatusPayload>());
        omsInstances[payload.InstrumentId] = payload;
        OnInstancesUpdated?.Invoke();
    }

    public IEnumerable<InstanceStatusPayload> GetAllActiveInstances()
    {
        return _activeInstancesByOms.Values.SelectMany(dict => dict.Values);
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
        foreach (var fill in payload.Fills)
        {
            // A unique key for a fill is typically its execution ID.
            // If ExecutionId can be null, you might need a composite key.
            var uniqueKey = fill.ExecutionId;
            if (string.IsNullOrEmpty(uniqueKey))
            {
                // Fallback key if ExecutionId is not available
                uniqueKey = $"{fill.ExchangeOrderId}-{fill.ClientOrderId}-{fill.Timestamp}";
            }

            // --- THIS IS THE DUPLICATE CHECK ---
            // TryAdd returns false if the key already exists.
            if (_fillsByExecutionId.TryAdd(uniqueKey, fill))
            {
                // If successfully added, also add the key to the queue to track order.
                _fillExecutionIdQueue.Enqueue(uniqueKey);

                // Fire the event for the new fill.
                OnFillReceived?.Invoke((payload.OmsIdentifier, fill));
            }
            else
            {
                // This fill is a duplicate, so we ignore it.
                // You could add a log here for debugging if needed.
            }
        }

        // After adding, enforce the size limit.
        while (_fillExecutionIdQueue.Count > MaxFillsToStore)
        {
            // Remove the oldest key from the queue.
            if (_fillExecutionIdQueue.TryDequeue(out var oldestKey))
            {
                // Remove the corresponding fill from the dictionary.
                _fillsByExecutionId.TryRemove(oldestKey, out _);
            }
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
        return _fillsByExecutionId.Values.OrderByDescending(f => f.Timestamp);
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
