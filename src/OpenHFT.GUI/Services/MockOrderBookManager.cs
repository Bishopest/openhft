using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.GUI.Services;

public class MockOrderBookManager : IOrderBookManager
{
    public event EventHandler<OrderBook>? OnOrderBookUpdated;

    private readonly List<int> _subscribedIds = new();
    private OrderBook? _currentOrderBook;

    public Task ConnectAndSubscribeAsync(int instrumentId)
    {
        if (_subscribedIds.Contains(instrumentId))
            return Task.CompletedTask;

        _subscribedIds.Add(instrumentId);

        // Create a fake OrderBook instance to display.
        var mockInstrument = new CryptoPerpetual(
                instrumentId: 1001,
                symbol: "BTCUSDT",
                exchange: ExchangeEnum.BINANCE,
                baseCurrency: Currency.BTC,
                quoteCurrency: Currency.USDT,
                tickSize: Price.FromDecimal(0.1m),
                lotSize: Quantity.FromDecimal(0.001m),
                multiplier: 1m,
                minOrderSize: Quantity.FromDecimal(0.001m)
        );

        var mockOrderBook = new OrderBook(mockInstrument);

        // Populate the mock book with some data
        var midPrice = 60000m;

        for (int i = 1; i <= 25; i++)
        {
            var askPrice = midPrice + (i * 0.01m);
            var bidPrice = midPrice - (i * 0.01m);
            var askQuantity = (25 - i) * 0.1m;
            var bidQuantity = i * 0.1m;
            var updates = new PriceLevelEntryArray();
            updates[0] = new PriceLevelEntry(Side.Sell, askPrice, askQuantity);
            updates[1] = new PriceLevelEntry(Side.Buy, bidPrice, bidQuantity);
            var marketDataEvent = new MarketDataEvent(
                sequence: i,
                timestamp: TimestampUtils.GetTimestampMicros(),
                kind: EventKind.Add,
                instrumentId: instrumentId,
                exchange: ExchangeEnum.BINANCE,
                updateCount: 2,
                updates: updates
            );

            mockOrderBook.ApplyEvent(marketDataEvent);
        }
        _currentOrderBook = mockOrderBook;
        OnOrderBookUpdated?.Invoke(this, mockOrderBook);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _subscribedIds.Clear();
        return Task.CompletedTask;
    }

    public IEnumerable<int> GetSubscribedIds() => _subscribedIds;

    public OrderBook? GetOrderBook(int instrumentId)
    {
        return _currentOrderBook;
    }
}