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

    public override ExchangeEnum SourceExchange => ExchangeEnum.BITMEX;

    protected override bool IsHeartbeatEnabled => true;

    public BitmexAdapter(ILogger<BitmexAdapter> logger, ProductType type, IInstrumentRepository instrumentRepository, ExecutionMode executionMode) : base(logger, type, instrumentRepository, executionMode)
    {
    }

    protected override string GetBaseUrl()
    {
        return _executionMode switch
        {
            ExecutionMode.Realtime => "wss://direct.bitmex.com/realtime",
            ExecutionMode.Live => "wss://ws.bitmex.com/realtime",
            ExecutionMode.Testnet => "wss://ws.testnet.bitmex.com/realtime", // BitMEX Testnet WebSocket URL
            _ => throw new ArgumentOutOfRangeException(nameof(_executionMode), $"Unsupported execution mode: {_executionMode}")
        };
    }

    protected override void ConfigureWebsocket(ClientWebSocket websocket)
    {
        websocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    protected override async Task DoAuthenticateAsync(CancellationToken cancellationToken)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds();

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

    protected override Task DoSubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        return SendSubscriptionRequest("subscribe", subscriptions, cancellationToken);
    }

    protected override Task DoUnsubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        return SendSubscriptionRequest("unsubscribe", subscriptions, cancellationToken);
    }

    private Task SendSubscriptionRequest(string operation, IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
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
        // --- 1. 필수 필드 추출 및 검증 ---
        var symbol = exeJson.TryGetProperty("symbol", out var symEl) ? symEl.GetString() : null;
        if (string.IsNullOrEmpty(symbol))
        {
            throw new FeedParseException(SourceExchange, $"Symbol not found in execution message({exeJson}).", null, null);
        }

        var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
        if (instrument == null)
        {
            throw new FeedParseException(SourceExchange, $"Unknown instrument symbol '{symbol}' in execution message({exeJson}).", null, null);
        }

        var clOrdIDString = exeJson.TryGetProperty("clOrdID", out var clOrdEl) ? clOrdEl.GetString() : null;
        if (string.IsNullOrEmpty(clOrdIDString) || !long.TryParse(clOrdIDString, out var clientOrderId))
        {
            throw new FeedParseException(SourceExchange, $"clOrdID not found or invalid in execution message({exeJson}).", null, null);
        }

        // --- 2. 상태 및 데이터 필드 추출 ---
        var executionId = exeJson.TryGetProperty("trdMatchID", out var trdMatchEl) ? trdMatchEl.GetString() : null;
        var exchangeOrderId = exeJson.TryGetProperty("orderID", out var exOrdEl) ? exOrdEl.GetString() : null;
        var ordStatusStr = exeJson.TryGetProperty("ordStatus", out var statEl) ? statEl.GetString() : null;
        var timestampStr = exeJson.TryGetProperty("timestamp", out var tsEl) ? tsEl.GetString() : null;
        var sideStr = exeJson.TryGetProperty("side", out var sideEl) ? sideEl.GetString() : null;

        // --- 3. 값 변환 ---
        var status = ordStatusStr switch
        {
            "New" => OrderStatus.New,
            "Filled" => OrderStatus.Filled,
            "PartiallyFilled" => OrderStatus.PartiallyFilled,
            "Canceled" => OrderStatus.Cancelled,
            "Rejected" => OrderStatus.Rejected,
            _ => OrderStatus.Pending // Or log a warning for unknown status
        };

        var side = sideStr switch
        {
            "Buy" => Side.Buy,
            "Sell" => Side.Sell,
            _ => throw new FeedParseException(SourceExchange, $"Invalid side value in execution message({exeJson}).", null, null)
        };

        var timestamp = !string.IsNullOrEmpty(timestampStr)
            ? DateTimeOffset.Parse(timestampStr).ToUnixTimeMilliseconds()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // GetProperty는 실패 시 예외를 던지므로, TryGetProperty를 사용하는 것이 더 안전합니다.
        exeJson.TryGetProperty("price", out var priceEl);
        exeJson.TryGetProperty("orderQty", out var qtyEl);
        exeJson.TryGetProperty("leavesQty", out var leavesEl);
        exeJson.TryGetProperty("lastQty", out var lastQtyEl);

        var price = Price.FromDecimal(priceEl.ValueKind == JsonValueKind.Number ? priceEl.GetDecimal() : 0);
        var quantity = Quantity.FromDecimal(qtyEl.ValueKind == JsonValueKind.Number ? qtyEl.GetDecimal() : 0);
        var leavesQuantity = Quantity.FromDecimal(leavesEl.ValueKind == JsonValueKind.Number ? leavesEl.GetDecimal() : 0);
        var lastQuantity = lastQtyEl.ValueKind == JsonValueKind.Number ? (Quantity?)Quantity.FromDecimal(lastQtyEl.GetDecimal()) : null;

        var rejectReason = exeJson.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;

        // --- 4. 최종 OrderStatusReport 객체 생성 ---
        var report = new OrderStatusReport(
            clientOrderId: clientOrderId,
            exchangeOrderId: exchangeOrderId,
            executionId: executionId,
            instrumentId: instrument.InstrumentId,
            side: side,
            status: status,
            price: price,
            quantity: quantity,
            leavesQuantity: leavesQuantity,
            timestamp: timestamp,
            rejectReason: rejectReason,
            lastQuantity: lastQuantity
        );

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
            if (root.TryGetProperty("error", out var errorElement))
            {
                var errorMessage = errorElement.GetString() ?? "Unknown error.";
                var statusCode = root.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out int code)
                    ? code
                    : 0;

                _logger.LogWarningWithCaller($"BitMEX WebSocket API Error (Status {statusCode}): {errorMessage}");
            }
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
