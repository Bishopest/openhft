using System;
using System.Collections.Concurrent;
using System.Text.Json;
using OpenHFT.Oms.Api.WebSocket;
using OpenHFT.Quoting.Models;

namespace OpenHFT.GUI.Services;

public class QuoteManager : IQuoteManager, IDisposable
{
    private readonly IOmsConnectorService _connector;
    private readonly JsonSerializerOptions _jsonOptions;

    // --- KEY CHANGE: Outer key is OmsIdentifier, inner key is InstrumentId ---
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, QuotePair>> _latestQuotes = new();

    public event EventHandler<(string omsIdentifier, QuotePair pair)>? OnQuoteUpdated;

    public QuoteManager(IOmsConnectorService connector, JsonSerializerOptions jsonOptions)
    {
        _connector = connector;
        // Subscribe to the new event from OmsConnectorService
        _connector.OnMessageReceived += HandleRawMessage;
        _jsonOptions = jsonOptions;
    }

    private void HandleRawMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();

        switch (type)
        {
            case "QUOTEPAIR_UPDATE":
                var updateEvent = JsonSerializer.Deserialize<QuotePairUpdateEvent>(json, _jsonOptions);
                if (updateEvent != null) HandleQuoteUpdate(updateEvent);
                break;
            default:
                break;
        }
    }


    private void HandleQuoteUpdate(QuotePairUpdateEvent updateEvent)
    {
        var payload = updateEvent.Payload;
        var omsOrders = _latestQuotes.GetOrAdd(payload.OmsIdentifier, new ConcurrentDictionary<int, QuotePair>());
        omsOrders[payload.QuotePair.InstrumentId] = payload.QuotePair;
        OnQuoteUpdated?.Invoke(this, (payload.OmsIdentifier, payload.QuotePair));
    }

    public QuotePair? GetQuote(string omsIdentifier, int instrumentId)
    {
        if (!_latestQuotes.TryGetValue(omsIdentifier, out var omsOrders)) return null;
        if (!omsOrders.TryGetValue(instrumentId, out var quotePair)) return null;
        return quotePair;
    }

    public void Dispose()
    {
        _connector.OnMessageReceived -= HandleRawMessage;
    }
}