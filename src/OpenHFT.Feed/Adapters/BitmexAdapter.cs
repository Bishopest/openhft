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

namespace OpenHFT.Feed.Adapters;

public class BitmexAdapter : BaseAuthFeedAdapter
{
    public override ExchangeEnum SourceExchange => ExchangeEnum.BITMEX;
    protected override bool IsHeartbeatEnabled => true;
    // Field to track the last execution ID per order id 
    private readonly ConcurrentDictionary<string, HashSet<string>> _processedExecIDs = new();
    // BitMEX L2 OrderBook ID to Price mapping cache
    // Key: BitMEX ID (long), Value: Price (decimal)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, decimal>> _l2IdToPrice = new();
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

    // --------------------------------------------------------------------------------
    // Execution (주문 상태 업데이트)
    // --------------------------------------------------------------------------------
    private void ProcessExecution_Raw(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            string? orderId = null, clOrdId = null, execId = null, symbol = null, ordStatus = null;
            decimal price = 0, lastPx = 0, orderQty = 0, leavesQty = 0, lastQty = 0;
            Side side = Side.Buy;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string? execType = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var prop = reader.ValueSpan;
                reader.Read();

                if (prop.SequenceEqual("orderID"u8)) orderId = reader.GetString();
                else if (prop.SequenceEqual("clOrdID"u8)) clOrdId = reader.GetString();
                else if (prop.SequenceEqual("trdMatchID"u8)) execId = reader.GetString();
                else if (prop.SequenceEqual("symbol"u8)) symbol = reader.GetString();
                else if (prop.SequenceEqual("side"u8)) side = reader.ValueTextEquals("Buy"u8) ? Side.Buy : Side.Sell;
                else if (prop.SequenceEqual("ordStatus"u8)) ordStatus = reader.GetString();
                else if (prop.SequenceEqual("execType"u8)) execType = reader.GetString();
                else if (prop.SequenceEqual("price"u8)) FastJsonParser.TryParseDecimal(ref reader, out price);
                else if (prop.SequenceEqual("lastPx"u8)) FastJsonParser.TryParseDecimal(ref reader, out lastPx);
                else if (prop.SequenceEqual("orderQty"u8)) FastJsonParser.TryParseDecimal(ref reader, out orderQty);
                else if (prop.SequenceEqual("leavesQty"u8)) FastJsonParser.TryParseDecimal(ref reader, out leavesQty);
                else if (prop.SequenceEqual("lastQty"u8)) FastJsonParser.TryParseDecimal(ref reader, out lastQty);
                else if (prop.SequenceEqual("timestamp"u8)) ts = reader.GetDateTimeOffset().ToUnixTimeMilliseconds();
            }

            // 중복 및 필터링 로직 (기존 로직 유지)
            if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(clOrdId) || string.IsNullOrEmpty(execId) || execType != "Trade") continue;
            var execIdSet = _processedExecIDs.GetOrAdd(orderId, _ => new HashSet<string>());
            if (!execIdSet.Add(execId))
            {
                _logger.LogWarningWithCaller($"duplicated execId {execId} for order {orderId}");
                continue;
            }

            if (!long.TryParse(clOrdId, out var clientOrderId))
            {
                _logger.LogWarningWithCaller($"clOrdID({clOrdId}) not found on {SourceExchange}.");
                continue;
            }

            if (string.IsNullOrEmpty(symbol))
            {
                _logger.LogWarningWithCaller($"symbol parsed to null when processing execution message order-id({orderId})");
                continue;
            }
            var inst = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
            if (inst == null) continue;

            var status = ordStatus switch
            {
                "New" => OrderStatus.New,
                "Filled" => OrderStatus.Filled,
                "PartiallyFilled" => OrderStatus.PartiallyFilled,
                "Canceled" => OrderStatus.Cancelled,
                "Rejected" => OrderStatus.Rejected,
                "PendingReplace" => OrderStatus.PartiallyFilled,
                _ => HandleUnknownStatus(ordStatus) // Or log a warning for unknown status
            };

            // Report 생성 및 전송
            var report = new OrderStatusReport(
                clientOrderId: clientOrderId,
                exchangeOrderId: orderId,
                executionId: execId,
                instrumentId: inst.InstrumentId,
                side: side,
                status: status,
                price: Price.FromDecimal(price),
                quantity: Quantity.FromDecimal(orderQty),
                leavesQuantity: Quantity.FromDecimal(leavesQty),
                timestamp: ts,
                lastPrice: Price.FromDecimal(lastPx),
                lastQuantity: Quantity.FromDecimal(lastQty)
            );
            OnOrderUpdateReceived(report);
            _logger.LogInformationWithCaller($"Processed new execution for order report => {report}");

            // 터미널 상태 시 캐시 정리
            if (ordStatus is "Filled" or "Canceled" or "Rejected") _processedExecIDs.TryRemove(orderId, out _);
        }
    }

    private OrderStatus HandleUnknownStatus(string? status)
    {
        _logger.LogWarningWithCaller($"Unknown order status received: '{status}'. Returning OrderStatus.Pending.");
        return OrderStatus.Pending;
    }

    private void ProcessOrderBook10_Raw(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            int instId = -1;
            long ts = 0;
            var updates = new PriceLevelEntryArray();
            int count = 0;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var prop = reader.ValueSpan;
                reader.Read();

                if (prop.SequenceEqual("symbol"u8)) instId = _instrumentRepository.FindBySymbol(Encoding.UTF8.GetString(reader.ValueSpan), ProdType, SourceExchange)?.InstrumentId ?? -1;
                else if (prop.SequenceEqual("timestamp"u8)) ts = reader.GetDateTimeOffset().ToUnixTimeMilliseconds();
                else if (prop.SequenceEqual("bids"u8) || prop.SequenceEqual("asks"u8))
                {
                    var side = prop.SequenceEqual("bids"u8) ? Side.Buy : Side.Sell;
                    if (reader.TokenType != JsonTokenType.StartArray) continue;

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        // [price, size] 내부 배열
                        reader.Read(); // Price
                        FastJsonParser.TryParseDecimal(ref reader, out decimal p);
                        reader.Read(); // Size
                        FastJsonParser.TryParseDecimal(ref reader, out decimal s);
                        reader.Read(); // End inner array

                        if (count < 40) updates[count++] = new PriceLevelEntry(side, p, s);
                    }
                }
            }

            if (instId != -1 && count > 0)
            {
                OnMarketDataReceived(new MarketDataEvent(0, ts, EventKind.Snapshot, instId, SourceExchange, 0, BitmexTopic.OrderBook10.TopicId, count, updates));
            }
        }
    }

    // --------------------------------------------------------------------------------
    // OrderBookL2_25 (가장 빈번함)
    // --------------------------------------------------------------------------------
    private void ProcessOrderBookL2_25_Raw(ref Utf8JsonReader reader, ReadOnlySpan<byte> actionSpan)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return;

        // 1. Action 및 초기 설정
        bool isPartial = actionSpan.SequenceEqual("partial"u8);
        EventKind baseKind = isPartial ? EventKind.Snapshot :
                             actionSpan.SequenceEqual("insert"u8) ? EventKind.Add :
                             actionSpan.SequenceEqual("update"u8) ? EventKind.Update :
                             actionSpan.SequenceEqual("delete"u8) ? EventKind.Delete : EventKind.Update;

        var updatesArray = new PriceLevelEntryArray();
        int count = 0;
        int currentInstrumentId = -1;
        long currentTimestamp = 0;
        ConcurrentDictionary<long, decimal>? symbolCache = null;

        bool isFirstChunkOfPartial = true;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            long id = 0;
            decimal price = 0, size = 0;
            Side side = Side.Buy;
            string? itemSymbol = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var prop = reader.ValueSpan;
                reader.Read();

                if (prop.SequenceEqual("symbol"u8)) itemSymbol = reader.GetString();
                else if (prop.SequenceEqual("id"u8)) id = reader.GetInt64();
                else if (prop.SequenceEqual("side"u8)) side = reader.ValueTextEquals("Buy"u8) ? Side.Buy : Side.Sell;
                else if (prop.SequenceEqual("size"u8)) FastJsonParser.TryParseDecimal(ref reader, out size);
                else if (prop.SequenceEqual("price"u8)) FastJsonParser.TryParseDecimal(ref reader, out price);
                else if (prop.SequenceEqual("timestamp"u8)) currentTimestamp = reader.GetDateTimeOffset().ToUnixTimeMilliseconds();
            }

            if (string.IsNullOrEmpty(itemSymbol)) continue;

            // 종목별 캐시 관리
            if (symbolCache == null)
            {
                symbolCache = _l2IdToPrice.GetOrAdd(itemSymbol, _ => new ConcurrentDictionary<long, decimal>());
                if (isPartial)
                {
                    symbolCache.Clear(); // 재연결 시 ID-Price 매핑 초기화
                }
            }

            var inst = _instrumentRepository.FindBySymbol(itemSymbol, ProdType, SourceExchange);
            if (inst == null) continue;
            currentInstrumentId = inst.InstrumentId;

            // 가격 결정 로직 (ID 매핑 활용)
            if (baseKind == EventKind.Snapshot || baseKind == EventKind.Add)
            {
                if (price != 0) symbolCache[id] = price;
            }
            else if (baseKind == EventKind.Update)
            {
                if (price != 0) symbolCache[id] = price;
                else symbolCache.TryGetValue(id, out price);
            }
            else if (baseKind == EventKind.Delete)
            {
                symbolCache.TryRemove(id, out price);
            }

            if (price == 0) continue;

            updatesArray[count++] = new PriceLevelEntry(side, price, size);

            // 40개가 찼을 때 전송 로직
            if (count >= 40)
            {
                // [핵심 로직] partial일 경우 첫 번째만 Snapshot(Clear용), 나머지는 Update(누적용)
                var currentEventKind = (isPartial && !isFirstChunkOfPartial) ? EventKind.Update : baseKind;

                OnMarketDataReceived(new MarketDataEvent(
                    0, currentTimestamp, currentEventKind, currentInstrumentId,
                    SourceExchange, 0, BitmexTopic.OrderBookL2_25.TopicId, count, updatesArray));

                count = 0;
                isFirstChunkOfPartial = false; // 이후 묶음부터는 지우지 마라
                updatesArray = new PriceLevelEntryArray();
            }
        }

        // 루프 종료 후 남은 데이터 처리
        if (count > 0)
        {
            var currentEventKind = (isPartial && !isFirstChunkOfPartial) ? EventKind.Update : baseKind;

            OnMarketDataReceived(new MarketDataEvent(
                0, currentTimestamp, currentEventKind, currentInstrumentId,
                SourceExchange, 0, BitmexTopic.OrderBookL2_25.TopicId, count, updatesArray));
        }
    }

    private void ProcessQuote_Raw(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            decimal bPrice = 0, bSize = 0, aPrice = 0, aSize = 0;
            int instId = -1;
            long ts = 0;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var prop = reader.ValueSpan;
                reader.Read();

                if (prop.SequenceEqual("symbol"u8)) instId = _instrumentRepository.FindBySymbol(Encoding.UTF8.GetString(reader.ValueSpan), ProdType, SourceExchange)?.InstrumentId ?? -1;
                else if (prop.SequenceEqual("bidPrice"u8)) FastJsonParser.TryParseDecimal(ref reader, out bPrice);
                else if (prop.SequenceEqual("bidSize"u8)) FastJsonParser.TryParseDecimal(ref reader, out bSize);
                else if (prop.SequenceEqual("askPrice"u8)) FastJsonParser.TryParseDecimal(ref reader, out aPrice);
                else if (prop.SequenceEqual("askSize"u8)) FastJsonParser.TryParseDecimal(ref reader, out aSize);
                else if (prop.SequenceEqual("timestamp"u8)) ts = reader.GetDateTimeOffset().ToUnixTimeMilliseconds();
            }

            if (instId == -1) continue;

            var updates = new PriceLevelEntryArray();
            updates[0] = new PriceLevelEntry(Side.Buy, bPrice, bSize);
            updates[1] = new PriceLevelEntry(Side.Sell, aPrice, aSize);

            OnMarketDataReceived(new MarketDataEvent(0, ts, EventKind.Snapshot, instId, SourceExchange, 0, BitmexTopic.Quote.TopicId, 2, updates));
        }
    }

    // --------------------------------------------------------------------------------
    // Trade 처리
    // --------------------------------------------------------------------------------
    private void ProcessTrade_Raw(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            decimal price = 0, size = 0;
            Side side = Side.Buy;
            int instId = -1;
            long ts = 0;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var prop = reader.ValueSpan;
                reader.Read();

                if (prop.SequenceEqual("symbol"u8)) instId = _instrumentRepository.FindBySymbol(Encoding.UTF8.GetString(reader.ValueSpan), ProdType, SourceExchange)?.InstrumentId ?? -1;
                else if (prop.SequenceEqual("side"u8)) side = reader.ValueTextEquals("Buy"u8) ? Side.Buy : Side.Sell;
                else if (prop.SequenceEqual("size"u8)) FastJsonParser.TryParseDecimal(ref reader, out size);
                else if (prop.SequenceEqual("price"u8)) FastJsonParser.TryParseDecimal(ref reader, out price);
                else if (prop.SequenceEqual("timestamp"u8)) ts = reader.GetDateTimeOffset().ToUnixTimeMilliseconds();
            }

            if (instId == -1)
            {
                OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Invalid symbol in depth message", null, null, BitmexTopic.Trade.TopicId), null));
                continue;
            }

            var updates = new PriceLevelEntryArray();
            updates[0] = new PriceLevelEntry(side, price, size);
            OnMarketDataReceived(new MarketDataEvent(0, ts, EventKind.Trade, instId, SourceExchange, 0, BitmexTopic.Trade.TopicId, 1, updates));
        }
    }

    protected override async Task ProcessMessage(ReadOnlyMemory<byte> messageBytes)
    {
        try
        {
            var reader = new Utf8JsonReader(messageBytes.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;

            // 변수 초기화
            ReadOnlySpan<byte> tableName = default;
            ReadOnlySpan<byte> actionName = default;
            bool isSuccessTrue = false;
            bool shouldExitEarly = false;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                // 프로퍼티 이름 (예: "table", "action", "data")
                var propName = reader.ValueSpan;

                // 값으로 이동
                if (!reader.Read()) break;

                // 1. 제어 메시지 처리
                if (propName.SequenceEqual("error"u8))
                {
                    _logger.LogWarningWithCaller($"BitMEX Error: {reader.GetString()}");
                }
                else if (propName.SequenceEqual("success"u8))
                {
                    isSuccessTrue = reader.GetBoolean();
                    shouldExitEarly = true;
                }
                else if (propName.SequenceEqual("subscribe"u8))
                {
                    shouldExitEarly = true;
                }
                // 2. 인증 요청 확인
                else if (propName.SequenceEqual("request"u8))
                {
                    if (isSuccessTrue && reader.TokenType == JsonTokenType.StartObject)
                    {
                        if (CheckIfAuthLimitRequest(ref reader))
                        {
                            _logger.LogInformationWithCaller("BitMEX WebSocket authentication successful.");
                            OnAuthenticationStateChanged(true, "Authentication successful.");
                        }
                    }
                }
                // 3. 테이블 및 액션 정보 저장
                else if (propName.SequenceEqual("table"u8))
                {
                    tableName = reader.ValueSpan; // "orderBookL2_25" 저장
                }
                else if (propName.SequenceEqual("action"u8))
                {
                    actionName = reader.ValueSpan; // "partial", "update" 등 저장
                }
                // 4. 핵심 데이터 파싱
                else if (propName.SequenceEqual("data"u8))
                {
                    if (shouldExitEarly) return;

                    // [핵심 수정] 저장해둔 tableName과 actionName을 사용하여 라우팅
                    if (tableName.SequenceEqual("orderBookL2_25"u8))
                        ProcessOrderBookL2_25_Raw(ref reader, actionName);
                    else if (tableName.SequenceEqual("trade"u8))
                        ProcessTrade_Raw(ref reader);
                    else if (tableName.SequenceEqual("quote"u8))
                        ProcessQuote_Raw(ref reader);
                    else if (tableName.SequenceEqual("orderBook10"u8))
                        ProcessOrderBook10_Raw(ref reader);
                    else if (tableName.SequenceEqual("execution"u8))
                        ProcessExecution_Raw(ref reader);

                    // data 필드는 보통 메시지의 마지막이므로 파싱 후 종료
                    break;
                }
                else
                {
                    reader.TrySkip();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Optimized parsing failed.");
        }
    }

    // request.op == "authKeyExpires" 인지 확인하는 헬퍼
    private bool CheckIfAuthLimitRequest(ref Utf8JsonReader reader)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("op"u8))
            {
                reader.Read();
                return reader.ValueTextEquals("authKeyExpires"u8);
            }
        }
        return false;
    }

    protected override string? GetPingMessage()
    {
        return "ping";
    }

    protected override bool IsPongMessage(ReadOnlySpan<byte> messageSpan)
    {
        // 1. 길이 체크 (4바이트가 아니면 "pong"일 리 없음)
        if (messageSpan.Length != 4)
        {
            return false;
        }

        // 2. 바이트 단위 비교 (Zero-allocation)
        // "pong"u8은 C# 11+의 기능으로, 문자열 생성 없이 
        // 컴파일 타임에 ReadOnlySpan<byte>로 직접 변환됩니다.
        return messageSpan.SequenceEqual("pong"u8);

        /* 만약 C# 11 이전 버전을 사용 중이라면 아래와 같이 비교합니다:
        return messageSpan[0] == (byte)'p' && 
               messageSpan[1] == (byte)'o' && 
               messageSpan[2] == (byte)'n' && 
               messageSpan[3] == (byte)'g';
        */
    }
}
