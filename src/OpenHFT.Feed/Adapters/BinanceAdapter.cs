using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Exceptions;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;
using OpenHFT.Gateway.ApiClient;

namespace OpenHFT.Feed.Adapters;

/// <summary>
/// Binance WebSocket feed adapter for real-time market data
/// Supports both individual symbol streams and combined streams
/// </summary>
public class BinanceAdapter : BaseAuthFeedAdapter
{
    private readonly BinanceRestApiClient _apiClient;
    private readonly ConcurrentDictionary<int, BinanceBookManager> _bookManagers = new();
    private string _listenKey;
    public override ExchangeEnum SourceExchange => ExchangeEnum.BINANCE;

    public BinanceAdapter(ILogger<BinanceAdapter> logger, ProductType type, IInstrumentRepository instrumentRepository, ExecutionMode executionMode, BinanceRestApiClient apiClient) : base(logger, type, instrumentRepository, executionMode)
    {
        _apiClient = apiClient;
    }

    protected override async Task DoAuthenticateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Requesting a new listen key from Binance REST API...");

        var listenKeyResponse = await _apiClient.CreateListenKeyAsync(ProdType, cancellationToken);
        _listenKey = listenKeyResponse.ListenKey;

        _logger.LogInformationWithCaller("Successfully obtained listen key from Binance.");
    }

    private void ProcessAggTrade(JsonElement data)
    {
        var symbol = data.GetProperty("s").GetString();
        if (symbol == null)
        {
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Invalid symbol in aggTrade message", null, null, BinanceTopic.AggTrade.TopicId), null));
            return;
        }
        var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
        if (instrument == null)
        {
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Invalid instrument(symbol: {symbol}) in aggTrade message", null, null, BinanceTopic.AggTrade.TopicId), null));
            return;
        }

        // Price and quantity are sent as strings in the aggTrade stream.
        if (decimal.TryParse(data.GetProperty("p").GetString(), out var price) &&
            decimal.TryParse(data.GetProperty("q").GetString(), out var quantity))
        {
            var tradeTime = data.GetProperty("T").GetInt64(); // Milliseconds
            var isBuyerMaker = data.GetProperty("m").GetBoolean();

            var updatesArray = new PriceLevelEntryArray();
            var tradeEntry = new PriceLevelEntry(side: isBuyerMaker ? Side.Sell : Side.Buy, priceTicks: price, quantity: quantity);
            updatesArray[0] = tradeEntry;
            var marketDataEvent = new MarketDataEvent(
                sequence: 0, // aggTrade doesn't have a clear sequence like depth updates
                timestamp: tradeTime,
                kind: EventKind.Trade,
                instrumentId: instrument.InstrumentId,
                exchange: SourceExchange,
                prevSequence: 0,
                topicId: BinanceTopic.AggTrade.TopicId,
                updateCount: 1,
                updates: updatesArray
            );

            OnMarketDataReceived(marketDataEvent);
        }
        else
        {
            _logger.LogWarningWithCaller($"Failed to parse price or quantity for aggTrade on symbol {symbol}");
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Failed to parse price or quantity for aggTrade on symbol {symbol}", null, null, BinanceTopic.AggTrade.TopicId), null));
            return;
        }
    }

    private void ProcessBookTicker(JsonElement data)
    {
        var symbol = data.GetProperty("s").GetString();
        if (symbol == null)
        {
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Invalid symbol in bookticker message", null, null, BinanceTopic.BookTicker.TopicId), null));
            return;
        }
        var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
        if (instrument == null)
        {
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Invalid instrument(symbol: {symbol}) in bookticker message", null, null, BinanceTopic.BookTicker.TopicId), null));
            return;
        }

        long eventTime;
        if (data.TryGetProperty("E", out var eventTimeElement) && eventTimeElement.TryGetInt64(out long parsedEventTime))
        {
            // Use the event time from the message (Perpetual Futures).
            eventTime = parsedEventTime;
        }
        else
        {
            // Fallback to current time when 'E' is not available (Spot).
            eventTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        var updatesArray = new PriceLevelEntryArray();

        // Process Best Bid
        if (decimal.TryParse(data.GetProperty("b").GetString(), out var bidPrice) &&
            decimal.TryParse(data.GetProperty("B").GetString(), out var bidQty) && bidQty > 0)
        {
            var tradeEntry = new PriceLevelEntry(side: Side.Buy, priceTicks: bidPrice, quantity: bidQty);
            updatesArray[0] = tradeEntry;
        }
        else
        {
            _logger.LogWarningWithCaller($"Failed to parse price or quantity for bid bookticker on symbol {symbol}");
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Failed to parse price or quantity for bid bookticker on symbol {symbol}", null, null, BinanceTopic.BookTicker.TopicId), null));
            return;
        }

        // Process Best Ask
        if (decimal.TryParse(data.GetProperty("a").GetString(), out var askPrice) &&
            decimal.TryParse(data.GetProperty("A").GetString(), out var askQty) && askQty > 0)
        {
            var tradeEntry = new PriceLevelEntry(side: Side.Sell, priceTicks: askPrice, quantity: askQty);
            updatesArray[1] = tradeEntry;
        }
        else
        {
            _logger.LogWarningWithCaller($"Failed to parse price or quantity for ask bookticker on symbol {symbol}");
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Failed to parse price or quantity for ask bookticker on symbol {symbol}", null, null, BinanceTopic.BookTicker.TopicId), null));
            return;
        }

        var bookTickerEvent = new MarketDataEvent(
                sequence: 0,
                timestamp: eventTime,
                kind: EventKind.Snapshot,
                instrumentId: instrument.InstrumentId,
                exchange: SourceExchange,
                topicId: BinanceTopic.BookTicker.TopicId,
                updateCount: 2,
                updates: updatesArray
            );
        OnMarketDataReceived(bookTickerEvent);
    }

    private void ProcessPartialDepthUpdate(JsonElement data)
    {
        var symbol = data.GetProperty("s").GetString();
        if (symbol == null)
        {
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Invalid symbol in depth message", null, null, BinanceTopic.DepthUpdate.TopicId), null));
            return;
        }
        var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
        if (instrument == null)
        {
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Invalid instrument(symbol: {symbol}) in depth message", null, null, BinanceTopic.DepthUpdate.TopicId), null));
            return;
        }

        var timestamp = data.GetProperty("E").GetInt64();
        var finalUpdateId = data.GetProperty("u").GetInt64();
        var prevUpdateId = data.GetProperty("pu").GetInt64();
        var updatesArray = new PriceLevelEntryArray();
        // Here you should check sequence continuity using 'pu' and 'U'/'u' as per Binance docs.
        // For simplicity, this example just processes the updates.

        int i = 0;
        // Process bids
        if (data.TryGetProperty("b", out var bidsElement))
        {
            foreach (var bid in bidsElement.EnumerateArray())
            {
                if (decimal.TryParse(bid[0].GetString(), out var price) &&
                    decimal.TryParse(bid[1].GetString(), out var quantity))
                {
                    var tradeEntry = new PriceLevelEntry(side: Side.Buy, priceTicks: price, quantity: quantity);
                    updatesArray[i] = tradeEntry;
                    i++;
                }
                else
                {
                    _logger.LogWarningWithCaller($"Failed to parse price or quantity for bid depth on symbol {symbol}");
                    OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Failed to parse price or quantity for bid depth on symbol {symbol}", null, null, BinanceTopic.DepthUpdate.TopicId), null));
                    return;
                }
            }
        }

        // Process asks
        if (data.TryGetProperty("a", out var asksElement))
        {
            foreach (var ask in asksElement.EnumerateArray())
            {
                if (decimal.TryParse(ask[0].GetString(), out var price) &&
                    decimal.TryParse(ask[1].GetString(), out var quantity))
                {
                    var tradeEntry = new PriceLevelEntry(side: Side.Sell, priceTicks: price, quantity: quantity);
                    updatesArray[i] = tradeEntry;
                    i++;
                }
                else
                {
                    _logger.LogWarningWithCaller($"Failed to parse price or quantity for ask depth on symbol {symbol}");
                    OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Failed to parse price or quantity for ask depth on symbol {symbol}", null, null, BinanceTopic.DepthUpdate.TopicId), null));
                    return;
                }
            }
        }

        OnMarketDataReceived(new MarketDataEvent(
                        sequence: finalUpdateId,
                        timestamp: timestamp,
                        kind: EventKind.Snapshot,
                        instrumentId: instrument.InstrumentId,
                        exchange: SourceExchange,
                        prevSequence: prevUpdateId,
                        topicId: BinanceTopic.DepthUpdate.TopicId,
                        updateCount: i,
                        updates: updatesArray
                    ));
    }

    private void ProcessDepthUpdate(JsonElement data)
    {
        var symbol = data.GetProperty("s").GetString();
        if (string.IsNullOrEmpty(symbol)) return;

        var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
        if (instrument == null) return;

        // Route the update to the correct book manager.
        if (_bookManagers.TryGetValue(instrument.InstrumentId, out var bookManager))
        {
            bookManager.ProcessWsUpdate(data);
        }
        else
        {
            // This can happen if a message arrives before the subscription process is complete.
            _logger.LogWarningWithCaller($"Received depth update for {symbol} but book manager is not yet ready. Message will be dropped.");
        }
    }
    protected override string GetBaseUrl()
    {
        var baseUrl = ProdType switch
        {
            ProductType.PerpetualFuture => "wss://fstream.binance.com/stream",
            ProductType.Spot => "wss://stream.binance.com:9443/stream",
            _ => throw new InvalidOperationException($"Unsupported product type for Binance: {ProdType}")
        };

        if (!string.IsNullOrEmpty(_listenKey))
        {
            return $"{baseUrl}?streams={_listenKey}";
        }

        return baseUrl;
    }

    protected override async Task ProcessMessage(ReadOnlyMemory<byte> messageBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(messageBytes);
            var root = document.RootElement;

            // Subscription response, ignore it.
            if (root.TryGetProperty("result", out _) || root.TryGetProperty("id", out _))
            {
                // Start the sync process in the background for each instrument.
                foreach (var instrument in _bookManagers.Keys)
                {
                    var bookManager = _bookManagers[instrument];
                    _ = bookManager.StartSyncAsync(CancellationToken.None);
                }
                return;
            }

            // Combined stream format: {"stream":"<streamName>","data":{...}}
            if (root.TryGetProperty("stream", out var streamElement) &&
                root.TryGetProperty("data", out var dataElement) &&
                streamElement.GetString() is { } streamName)
            {
                int atIndex = streamName.IndexOf('@');
                if (atIndex > 0)
                {
                    var streamSuffix = streamName.Substring(atIndex);

                    var topic = BinanceTopic.GetAllMarketTopics()
                        .FirstOrDefault(t => t.GetStreamName("").Equals(streamSuffix, StringComparison.OrdinalIgnoreCase));

                    if (topic != null)
                    {
                        // Now we have the correct topic object.
                        if (topic == BinanceTopic.AggTrade) ProcessAggTrade(dataElement);
                        else if (topic == BinanceTopic.BookTicker) ProcessBookTicker(dataElement);
                        else if (topic == BinanceTopic.DepthUpdate) ProcessDepthUpdate(dataElement);
                    }
                }
            }
        }
        catch (JsonException jex)
        {
            // Handle parsing errors
            OnError(new FeedErrorEventArgs(new FeedReceiveException(SourceExchange, "JSON parsing failed.", jex), null));
        }
        catch (Exception ex)
        {
            // Handle other processing errors
            OnError(new FeedErrorEventArgs(new FeedReceiveException(SourceExchange, "Failed to process message.", ex), null));
        }
    }

    private string GetRawMessage(MemoryStream ms)
    {
        ms.Position = 0;
        using var reader = new StreamReader(ms, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    protected override async Task DoSubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        var depthUpdateTopic = subscriptions
            .SelectMany(kvp => kvp.Value)
            .FirstOrDefault(t => t == BinanceTopic.DepthUpdate);

        if (depthUpdateTopic != null)
        {
            foreach (var instrument in subscriptions.Keys)
            {
                var bookManager = _bookManagers.GetOrAdd(instrument.InstrumentId,
                    _ => new BinanceBookManager(_logger, _apiClient, instrument.InstrumentId, OnMarketDataReceived));


            }
        }

        var allStreams = subscriptions
            .SelectMany(kvp =>
            {
                var instrument = kvp.Key;
                var topics = kvp.Value;
                return topics.Select(topic => topic.GetStreamName(instrument.Symbol));
            })
            .Distinct()
            .ToArray();

        if (!allStreams.Any())
        {
            return;
        }

        var subscribeMessage = JsonSerializer.Serialize(new
        {
            method = "SUBSCRIBE",
            @params = allStreams,
            id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        _logger.LogInformationWithCaller($"Sending Binance subscribe request: {string.Join(", ", allStreams)}");
        await SendMessageAsync(subscribeMessage, cancellationToken);

    }

    protected override Task DoUnsubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        foreach (var instrument in subscriptions.Keys)
        {
            _bookManagers.TryRemove(instrument.InstrumentId, out _);
        }

        var allStreams = subscriptions
            .SelectMany(kvp =>
            {
                var instrument = kvp.Key;
                var topics = kvp.Value;
                return topics.Select(topic => topic.GetStreamName(instrument.Symbol));
            })
            .Distinct() // 중복 스트림 이름 제거
            .ToArray();

        if (!allStreams.Any())
        {
            return Task.CompletedTask;
        }

        var unsubscribeMessage = JsonSerializer.Serialize(new
        {
            method = "UNSUBSCRIBE",
            @params = allStreams,
            id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        _logger.LogInformationWithCaller($"Unsubscribing from {allStreams.Length} streams on {Exchange.Decode(SourceExchange)}");
        return SendMessageAsync(unsubscribeMessage, cancellationToken);
    }

    protected override void ConfigureWebsocket(ClientWebSocket websocket)
    {
        websocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        websocket.Options.SetBuffer(8192, 8192); // 8KB buffers
    }

    protected override string? GetPingMessage()
    {
        // Not used for Binance.
        return null;
    }

    protected override bool IsPongMessage(ReadOnlySpan<byte> messageSpan)
    {
        // Not used for Binance.
        return false;
    }

    public override Task SubscribeToPrivateTopicsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("SubscribeToPrivateTopicsAsync is not implemented for BinanceAdapter.");
        return Task.CompletedTask;
    }
}
