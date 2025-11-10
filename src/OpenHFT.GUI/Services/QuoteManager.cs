using System;
using System.Collections.Concurrent;
using OpenHFT.Oms.Api.WebSocket;
using OpenHFT.Quoting.Models;

namespace OpenHFT.GUI.Services;

public class QuoteManager : IQuoteManager, IDisposable
{
    private readonly IOmsConnectorService _connector;
    private readonly ConcurrentDictionary<int, QuotePair> _latestQuotes = new();

    public event EventHandler<QuotePair>? OnQuoteUpdated;

    public QuoteManager(IOmsConnectorService connector)
    {
        _connector = connector;
        // Subscribe to the new event from OmsConnectorService
        _connector.OnQuotePairUpdateReceived += HandleQuoteUpdate;
    }

    private void HandleQuoteUpdate(QuotePairUpdateEvent updateEvent)
    {
        _latestQuotes[updateEvent.QuotePair.InstrumentId] = updateEvent.QuotePair;
        OnQuoteUpdated?.Invoke(this, updateEvent.QuotePair);
    }

    public QuotePair? GetQuote(int instrumentId)
    {
        return _latestQuotes.GetValueOrDefault(instrumentId);
    }

    public void Dispose()
    {
        _connector.OnQuotePairUpdateReceived -= HandleQuoteUpdate;
    }
}