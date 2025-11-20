using System;
using System.Collections.Concurrent;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.GUI.Services;

public class OrderBookManager : IOrderBookManager, IDisposable
{
    private readonly ILogger<OrderBookManager> _logger;
    private readonly IExchangeFeedManager _feedManager;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly ConcurrentDictionary<int, OrderBook> _orderBooks = new();
    public event EventHandler<OrderBook>? OnOrderBookUpdated;

    public OrderBookManager(ILogger<OrderBookManager> logger, IExchangeFeedManager feedManager, IInstrumentRepository instrumentRepository)
    {
        _logger = logger;
        _feedManager = feedManager;
        _instrumentRepository = instrumentRepository;
        _feedManager.OnMarketDataReceived += HandleMarketData;
    }

    private void HandleMarketData(object sender, MarketDataEvent mdEvent)
    {
        try
        {
            var orderBook = _orderBooks.GetOrAdd(mdEvent.InstrumentId, id =>
            {
                var inst = _instrumentRepository.GetById(id);
                if (inst is null) return null;

                return new OrderBook(inst);
            });

            if (orderBook is not null && orderBook.ApplyEvent(mdEvent))
            {
                OnOrderBookUpdated?.Invoke(this, orderBook);
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error processing market data event for instrument ID {mdEvent.InstrumentId}");
        }
    }

    public OrderBook? GetOrderBook(int instrumentId) => _orderBooks.GetValueOrDefault(instrumentId);

    public void Dispose() => _feedManager.OnMarketDataReceived -= HandleMarketData;

    public IEnumerable<int> GetSubscribedIds()
    {
        throw new NotImplementedException();
    }
}
