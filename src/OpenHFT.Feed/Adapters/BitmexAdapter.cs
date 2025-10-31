using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Exceptions;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;

namespace OpenHFT.Feed.Adapters;

public class BitmexAdapter : BaseAuthFeedAdapter
{
    private const string DefaultBaseUrl = "wss://ws.bitmex.com/realtime";

    public override ExchangeEnum SourceExchange => ExchangeEnum.BITMEX;

    protected override bool IsHeartbeatEnabled => true;

    public BitmexAdapter(ILogger<BitmexAdapter> logger, ProductType type, IInstrumentRepository instrumentRepository) : base(logger, type, instrumentRepository)
    {
    }

    protected override string GetBaseUrl()
    {
        return DefaultBaseUrl;
    }

    protected override void ConfigureWebsocket(ClientWebSocket websocket)
    {
        websocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    protected override async Task DoAuthenticateAsync(CancellationToken cancellationToken)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();

        var signatureString = $"GET/realtime{expires}";
        var signature = CreateSignature(signatureString); // Uses helper from BaseAuthFeedAdapter

        var authRequest = new
        {
            op = "authKeyExpires",
            args = new object[] { ApiKey!, expires, signature }
        };

        var message = JsonSerializer.Serialize(authRequest);
        _logger.LogInformationWithCaller("Sending authentication request to BitMEX WebSocket.");
        await SendMessageAsync(message, cancellationToken);

        await SubscribeToPrivateTopicsAsync(cancellationToken);
    }

    public override Task SubscribeToPrivateTopicsAsync(CancellationToken cancellationToken)
    {
        var privateTopics = BitmexTopic.GetAllPrivateTopics();
        var topicArgs = privateTopics.Select(t => t.GetStreamName("")).ToArray();
        if (!topicArgs.Any())
        {
            _logger.LogWarningWithCaller("No private topics defined for BitMEX; skipping subscription.");
            return Task.CompletedTask;
        }
        var subscriptionRequest = new
        {
            op = "subscribe",
            args = topicArgs
        };
        var message = JsonSerializer.Serialize(subscriptionRequest);
        _logger.LogInformationWithCaller($"Subscribing to private BitMEX topics: {string.Join(", ", topicArgs)}");
        return SendMessageAsync(message, cancellationToken);
    }

    protected override Task DoSubscribeAsync(IEnumerable<Instrument> insts, CancellationToken cancellationToken)
    {
        return SendSubscriptionRequest("subscribe", insts, cancellationToken);
    }

    protected override Task DoUnsubscribeAsync(IEnumerable<Instrument> insts, CancellationToken cancellationToken)
    {
        return SendSubscriptionRequest("unsubscribe", insts, cancellationToken);
    }

    private Task SendSubscriptionRequest(string operation, IEnumerable<Instrument> insts, CancellationToken cancellationToken)
    {
        var allStreams = insts
            .SelectMany(inst => BitmexTopic.GetAllMarketTopics().Select(topic => topic.GetStreamName(inst.Symbol)))
            .ToArray();

        if (!allStreams.Any())
        {
            return Task.CompletedTask;
        }

        var request = new
        {
            op = operation,
            args = allStreams
        };

        var message = JsonSerializer.Serialize(request);
        _logger.LogInformationWithCaller($"Sending Bitmex {operation} request: {message}");
        return SendMessageAsync(message, cancellationToken);
    }

    private void ProcessExecution(JsonElement data)
    {
        foreach (var exeJson in data.EnumerateArray())
        {
            try
            {
                var report = ParseExecution(exeJson);
                OnOrderUpdateReceived(report);
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCaller(ex, "Failed to process BitMEX execution message.");
            }
        }
    }
    private OrderStatusReport ParseExecution(JsonElement exeJson)
    {
        var symbol = exeJson.GetProperty("symbol").GetString();
        if (symbol == null)
        {
            throw new FeedParseException(SourceExchange, "Invalid symbol in execution message", null, null, BitmexTopic.Execution.TopicId);
        }

        var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
        if (instrument == null)
        {
            throw new FeedParseException(SourceExchange, $"Invalid instrument(symbol: {symbol}) in execution message", null, null, BitmexTopic.Execution.TopicId);
        }

        var execType = exeJson.GetProperty("execType").GetString();
        var orderStatus = execType switch
        {
            "New" => OrderStatus.New,
            "Filled" => OrderStatus.Filled,
            "PartiallyFilled" => OrderStatus.PartiallyFilled,
            _ => OrderStatus.New
        };

        var report = new OrderStatusReport();
        return report;
    }

    private void ProcessOrderBook10(JsonElement data)
    {
        if (!data.EnumerateArray().Any())
        {
            return;
        }

        // BitMEX의 orderBook10은 스냅샷이므로, 첫 번째 항목에서 instrument와 timestamp를 가져옵니다.
        foreach (var ele in data.EnumerateArray())
        {
            var symbol = ele.GetProperty("symbol").GetString();
            if (symbol == null)
            {
                OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Invalid symbol in depth message", null, null, BitmexTopic.OrderBook10.TopicId), null));
                return;
            }

            var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
            if (instrument == null)
            {
                OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Invalid instrument(symbol: {symbol}) in depth message", null, null, BitmexTopic.OrderBook10.TopicId), null));
                return;
            }

            var timestampStr = ele.GetProperty("timestamp").GetString();
            if (timestampStr == null)
            {
                OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Invalid timestamp in depth message", null, null, BitmexTopic.OrderBook10.TopicId), null));
                return;
            }

            DateTimeOffset dateTimeOffset = DateTimeOffset.Parse(timestampStr);
            long unixTimeMilliseconds = dateTimeOffset.ToUnixTimeMilliseconds();

            var updatesArray = new PriceLevelEntryArray();
            int i = 0;
            if (ele.TryGetProperty("bids", out var bidsElement))
            {
                foreach (var bid in bidsElement.EnumerateArray())
                {
                    var price = bid[0].GetDecimal();
                    var size = bid[1].GetDecimal();
                    var priceLevelEntry = new PriceLevelEntry(
                        side: Side.Buy,
                        priceTicks: price,
                        quantity: size
                    );
                    updatesArray[i] = priceLevelEntry;
                    i++;
                }
            }

            if (ele.TryGetProperty("asks", out var asksElement))
            {
                foreach (var ask in asksElement.EnumerateArray())
                {
                    var price = ask[0].GetDecimal();
                    var size = ask[1].GetDecimal();
                    var priceLevelEntry = new PriceLevelEntry(
                        side: Side.Sell,
                        priceTicks: price,
                        quantity: size
                    );
                    updatesArray[i] = priceLevelEntry;
                    i++;
                }
            }
            if (i > 0)
            {
                OnMarketDataReceived(new MarketDataEvent(
                    sequence: 0, // orderBook10은 sequence 번호를 제공하지 않습니다.
                    timestamp: unixTimeMilliseconds,
                    kind: EventKind.Snapshot,
                    instrumentId: instrument.InstrumentId,
                    exchange: SourceExchange,
                    prevSequence: 0,
                    topicId: BitmexTopic.OrderBook10.TopicId,
                    updateCount: i,
                    updates: updatesArray
                ));
            }
        }
    }

    private void ProcessQuote(JsonElement data)
    {
        if (!data.EnumerateArray().Any())
        {
            return;
        }

        foreach (var ele in data.EnumerateArray())
        {
            var symbol = ele.GetProperty("symbol").GetString();
            if (symbol == null)
            {
                OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Invalid symbol in depth message", null, null, BitmexTopic.Quote.TopicId), null));
                return;
            }

            var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
            if (instrument == null)
            {
                OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Invalid instrument(symbol: {symbol}) in depth message", null, null, BitmexTopic.Quote.TopicId), null));
                return;
            }

            var timestampStr = ele.GetProperty("timestamp").GetString();
            if (timestampStr == null)
            {
                OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Invalid timestamp in depth message", null, null, BitmexTopic.Quote.TopicId), null));
                return;
            }

            DateTimeOffset dateTimeOffset = DateTimeOffset.Parse(timestampStr);
            long unixTimeMilliseconds = dateTimeOffset.ToUnixTimeMilliseconds();

            var updatesArray = new PriceLevelEntryArray();
            var askPrice = ele.GetProperty("askPrice").GetDecimal();
            var askSize = ele.GetProperty("askSize").GetDecimal();
            var bidPrice = ele.GetProperty("bidPrice").GetDecimal();
            var bidSize = ele.GetProperty("bidSize").GetDecimal();

            var askLevelEntry = new PriceLevelEntry(
                side: Side.Sell,
                priceTicks: askPrice,
                quantity: askSize
            );
            var bidLevelEntry = new PriceLevelEntry(
                side: Side.Buy,
                priceTicks: bidPrice,
                quantity: bidSize
            );
            updatesArray[0] = askLevelEntry;
            updatesArray[1] = bidLevelEntry;
            OnMarketDataReceived(new MarketDataEvent(
                sequence: 0,
                timestamp: unixTimeMilliseconds,
                kind: EventKind.Snapshot,
                instrumentId: instrument.InstrumentId,
                exchange: SourceExchange,
                prevSequence: 0,
                topicId: BitmexTopic.Quote.TopicId,
                updateCount: 2,
                updates: updatesArray
            ));
        }
    }

    private void ProcessTrade(JsonElement data)
    {
        if (!data.EnumerateArray().Any())
        {
            return;
        }

        foreach (var ele in data.EnumerateArray())
        {
            var symbol = ele.GetProperty("symbol").GetString();
            if (symbol == null)
            {
                OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Invalid symbol in depth message", null, null, BitmexTopic.Trade.TopicId), null));
                return;
            }

            var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
            if (instrument == null)
            {
                OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, $"Invalid instrument(symbol: {symbol}) in depth message", null, null, BitmexTopic.Trade.TopicId), null));
                return;
            }

            var timestampStr = ele.GetProperty("timestamp").GetString();
            if (timestampStr == null)
            {
                OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Invalid timestamp in depth message", null, null, BitmexTopic.Trade.TopicId), null));
                return;
            }

            DateTimeOffset dateTimeOffset = DateTimeOffset.Parse(timestampStr);
            long unixTimeMilliseconds = dateTimeOffset.ToUnixTimeMilliseconds();

            var updatesArray = new PriceLevelEntryArray();
            var price = ele.GetProperty("price").GetDecimal();
            var size = ele.GetProperty("size").GetDecimal();
            var sideStr = ele.GetProperty("side").GetString();
            var priceLevelEntry = new PriceLevelEntry(
                side: sideStr == "Buy" ? Side.Buy : Side.Sell,
                priceTicks: price,
                quantity: size
            );
            updatesArray[0] = priceLevelEntry;
            OnMarketDataReceived(new MarketDataEvent(
                sequence: 0,
                timestamp: unixTimeMilliseconds,
                kind: EventKind.Trade,
                instrumentId: instrument.InstrumentId,
                exchange: SourceExchange,
                prevSequence: 0,
                topicId: BitmexTopic.Trade.TopicId,
                updateCount: 1,
                updates: updatesArray
            ));
        }
    }

    protected override async Task ProcessMessage(MemoryStream messageStream)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(messageStream);
            var root = document.RootElement;

            // Subscription response, ignore it.
            if (root.TryGetProperty("success", out _) || root.TryGetProperty("subscribe", out _))
            {
                return;
            }

            if (!root.TryGetProperty("table", out var tableElement))
            {
                return;
            }

            if (root.TryGetProperty("data", out var dataElement) &&
                TopicRegistry.TryGetTopic(tableElement.GetString(), out var topic))
            {
                if (topic == BitmexTopic.OrderBook10)
                {
                    ProcessOrderBook10(dataElement);
                }
                else if (topic == BitmexTopic.Quote)
                {
                    ProcessQuote(dataElement);
                }
                else if (topic == BitmexTopic.Trade)
                {
                    ProcessTrade(dataElement);
                }
                else if (topic == BitmexTopic.Execution)
                {
                    ProcessExecution(dataElement);
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

    protected override string? GetPingMessage()
    {
        return "ping";
    }

    protected override bool IsPongMessage(MemoryStream messageStream)
    {
        if (messageStream.Length != 4)
        {
            return false;
        }

        var originalPosition = messageStream.Position;
        Span<byte> buffer = stackalloc byte[4];
        var bytesRead = messageStream.Read(buffer);
        messageStream.Position = originalPosition; // IMPORTANT: Reset stream position

        if (bytesRead < 4)
        {
            return false;
        }

        // Compare byte-by-byte for "pong"
        return buffer[0] == 'p' && buffer[1] == 'o' && buffer[2] == 'n' && buffer[3] == 'g';
    }
}
