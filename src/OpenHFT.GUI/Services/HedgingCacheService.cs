using System;
using System.Collections.Concurrent;
using System.Text.Json;
using OpenHFT.Core.Configuration;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

public class HedgingCacheService : IHedgingCacheService, IDisposable
{
    private readonly IOmsConnectorService _connector;
    private readonly JsonSerializerOptions _jsonOptions;

    // Key: OmsIdentifier, Value: Dictionary of statuses (Key: QuotingInstrumentId)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, HedgingStatusPayload>> _hedgingStatuses = new();

    public event Action? OnHedgingStatusUpdated;

    public HedgingCacheService(IOmsConnectorService connector, JsonSerializerOptions jsonOptions)
    {
        _connector = connector;
        _jsonOptions = jsonOptions;
        _connector.OnMessageReceived += HandleRawMessage;
        _connector.OnConnectionStatusChanged += HandleConnectionStatusChange;
    }

    private void HandleRawMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();

        switch (type)
        {
            case "HEDGING_STATUS":
                var statusEvent = JsonSerializer.Deserialize<HedgingStatusEvent>(json, _jsonOptions);
                if (statusEvent != null)
                {
                    var payload = statusEvent.Payload;
                    var omsStatuses = _hedgingStatuses.GetOrAdd(payload.OmsIdentifier, new ConcurrentDictionary<int, HedgingStatusPayload>());
                    omsStatuses[payload.QuotingInstrumentId] = payload;
                    OnHedgingStatusUpdated?.Invoke();
                }
                break;
            default:
                break;
        }
    }

    public HedgingStatusPayload? GetHedgingStatus(string omsIdentifier, int quotingInstrumentId)
    {
        if (_hedgingStatuses.TryGetValue(omsIdentifier, out var omsStatuses))
        {
            omsStatuses.TryGetValue(quotingInstrumentId, out var status);
            return status;
        }
        return null;
    }

    private void HandleConnectionStatusChange((OmsServerConfig Server, ConnectionStatus Status) args)
    {
        if (args.Status == ConnectionStatus.Disconnected || args.Status == ConnectionStatus.Error)
        {
            ClearCacheForOms(args.Server.OmsIdentifier);
        }
    }

    public void ClearCacheForOms(string omsIdentifier)
    {
        if (_hedgingStatuses.TryRemove(omsIdentifier, out _))
        {
            OnHedgingStatusUpdated?.Invoke();
        }
    }

    public void Dispose()
    {
        _connector.OnMessageReceived -= HandleRawMessage;
        _connector.OnConnectionStatusChanged -= HandleConnectionStatusChange;
    }
}