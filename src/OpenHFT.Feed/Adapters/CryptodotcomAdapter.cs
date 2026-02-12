using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Models;

namespace OpenHFT.Feed.Adapters;

public class CryptodotcomAdapter : BaseFeedAdapter
{
    public override ExchangeEnum SourceExchange => ExchangeEnum.CRYPTODOTCOM;
    protected override bool IsHeartbeatEnabled => false; // 서버가 Heartbeat 주도함 (Ping-Pong 아님)
    private readonly ConcurrentDictionary<int, CryptodotcomBookManager> _bookManagers = new();

    public CryptodotcomAdapter(ILogger<CryptodotcomAdapter> logger, ProductType type, IInstrumentRepository instrumentRepository, ExecutionMode executionMode)
        : base(logger, type, instrumentRepository, executionMode)
    {
    }

    protected override string GetBaseUrl()
    {
        // Market Data Websocket Endpoints
        return _executionMode switch
        {
            ExecutionMode.Live => "wss://stream.crypto.com/exchange/v1/market",
            ExecutionMode.Testnet => "wss://uat-stream.3ona.co/exchange/v1/market", // UAT Sandbox
            _ => throw new ArgumentOutOfRangeException(nameof(_executionMode))
        };
    }

    protected override void ConfigureWebsocket(ClientWebSocket webSocket)
    {
        webSocket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
    }

    // --- Subscription Logic ---
    protected override async Task DoSubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        var bookChannels = new List<string>();
        var otherChannels = new List<string>();

        // 1. 채널 분류 (Book vs Others)
        foreach (var kvp in subscriptions)
        {
            var instrument = kvp.Key;
            foreach (var topic in kvp.Value)
            {
                var streamName = topic.GetStreamName(instrument.Symbol);

                // 토픽 이름이 "book"으로 시작하는지 확인
                if (topic == CryptodotcomTopic.OrderBook)
                {
                    bookChannels.Add(streamName);
                }
                else
                {
                    otherChannels.Add(streamName);
                }
            }
        }

        // 2. Book 채널 구독 요청 전송
        if (bookChannels.Any())
        {
            var bookRequest = new
            {
                id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                method = "subscribe",
                @params = new
                {
                    channels = bookChannels.ToArray(),
                    book_subscription_type = "SNAPSHOT_AND_UPDATE",
                    book_update_frequency = 10 // 10ms
                },
                nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var json = JsonSerializer.Serialize(bookRequest);
            _logger.LogInformationWithCaller($"Crypto.com Submitting (OrderBook): {json}");

            await SendMessageAsync(json, cancellationToken);
        }

        // 3. 딜레이 (Book 요청을 보냈고, 보낼 기타 요청도 남아있을 때만)
        if (bookChannels.Any() && otherChannels.Any())
        {
            // Rate Limit 방지를 위해 1초 대기
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        // 4. 기타 채널 구독 요청 전송
        if (otherChannels.Any())
        {
            var otherRequest = new
            {
                id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1,
                method = "subscribe",
                @params = new
                {
                    channels = otherChannels.ToArray()
                },
                nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var json = JsonSerializer.Serialize(otherRequest);
            _logger.LogInformationWithCaller($"Crypto.com Submitting (Others): {json}");

            await SendMessageAsync(json, cancellationToken);
        }
    }

    protected override Task DoUnsubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        // 구독 취소 로직 (구현 필요 시 추가, Crypto.com은 unsubscribe method 지원)
        return Task.CompletedTask;
    }

    // --- Parsing Logic (Zero-Allocation) ---
    protected override async Task ProcessMessage(ReadOnlyMemory<byte> messageBytes)
    {
        try
        {
            var reader = new Utf8JsonReader(messageBytes.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;

            string? method = null;
            long id = 0;

            // 1. 최상위 레벨 파싱 (method, result 등 확인)
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var prop = reader.ValueSpan;
                    reader.Read();

                    if (prop.SequenceEqual("method"u8))
                    {
                        method = reader.GetString();
                        if (method == "public/heartbeat")
                        {
                            await HandleHeartbeatAsync(id);
                            return;
                        }
                    }
                    else if (prop.SequenceEqual("id"u8))
                    {
                        id = reader.GetInt64();
                    }
                    else if (prop.SequenceEqual("result"u8))
                    {
                        // result 객체 내부 파싱 (구독 데이터)
                        if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            ParseResultObject(ref reader);
                        }
                    }
                    else if (prop.SequenceEqual("code"u8))
                    {
                        if (reader.GetInt32() != 0)
                        {
                            _logger.LogWarningWithCaller($"Crypto.com Error Code: {reader.GetInt32()} in message: {Encoding.UTF8.GetString(messageBytes.Span)}");
                            return;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Crypto.com parsing failed.");
        }
    }

    private async Task HandleHeartbeatAsync(long id)
    {
        // Heartbeat 응답: id를 그대로 돌려줘야 함
        var response = new
        {
            id = id,
            method = "public/respond-heartbeat"
        };
        await SendMessageAsync(JsonSerializer.Serialize(response), CancellationToken.None);
        _logger.LogInformationWithCaller("Responded to Crypto.com heartbeat.");
    }

    private void ParseResultObject(ref Utf8JsonReader reader)
    {
        // result: { instrument_name: "...", subscription: "...", channel: "...", data: [...] }
        string? instrumentName = null;
        string? channel = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.ValueSpan;
            reader.Read();

            if (prop.SequenceEqual("instrument_name"u8)) instrumentName = reader.GetString();
            else if (prop.SequenceEqual("channel"u8)) channel = reader.GetString();
            else if (prop.SequenceEqual("data"u8))
            {
                if (reader.TokenType == JsonTokenType.StartArray && instrumentName != null && channel != null)
                {
                    var inst = _instrumentRepository.FindBySymbol(instrumentName, ProdType, SourceExchange);
                    if (inst != null)
                    {
                        if (channel == "book" || channel == "book.update")
                            ProcessOrderBookData(ref reader, inst, channel == "book");
                        else if (channel == "trade")
                            ProcessTradeData(ref reader, inst);
                        else if (channel == "ticker")
                            ProcessTickerData(ref reader, inst);
                    }
                    else
                    {
                        reader.TrySkip(); // Skip data array if instrument not found
                    }
                }
                else
                {
                    reader.TrySkip();
                }
            }
        }
    }

    private void ProcessOrderBookData(ref Utf8JsonReader reader, Instrument inst, bool isSnapshot)
    {
        // data: [ { bids:[], asks:[], t:..., u:..., pu:... } ]
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) continue;

            var manager = _bookManagers.GetOrAdd(inst.InstrumentId,
                _ => new CryptodotcomBookManager(_logger, inst.InstrumentId, OnMarketDataReceived, () => TriggerResubscribe(inst)));

            // Manager에게 파싱 및 상태 관리를 위임 (ref reader 전달)
            manager.ProcessWsUpdate(ref reader, isSnapshot);
        }
    }

    private void TriggerResubscribe(Instrument instrument)
    {
        _logger.LogInformationWithCaller($"Resubscribing to OrderBook for {instrument.Symbol} due to GAP.");

        var topic = CryptodotcomTopic.OrderBook;
        var subMap = new Dictionary<Instrument, List<ExchangeTopic>>
        {
            { instrument, new List<ExchangeTopic> { topic } }
        };

        // DoSubscribeAsync 호출 (내부적으로 JSON 생성 및 전송)
        // Fire-and-forget 방식으로 호출하되 에러 로깅은 수행
        _ = DoSubscribeAsync(subMap, CancellationToken.None).ContinueWith(t =>
        {
            if (t.IsFaulted) _logger.LogErrorWithCaller(t.Exception, $"Failed to resubscribe for {instrument.Symbol} during gap recovery.");
        });
    }

    private void ParseOrderBookLevel(ref Utf8JsonReader reader, ref PriceLevelEntryArray updates, ref int count, ref long u, ref long pu, ref long t, bool isSnapshot)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.ValueSpan;
            reader.Read();

            if (prop.SequenceEqual("u"u8)) u = reader.GetInt64();
            else if (prop.SequenceEqual("pu"u8)) pu = reader.GetInt64();
            else if (prop.SequenceEqual("t"u8)) t = reader.GetInt64();
            else if (prop.SequenceEqual("update"u8) && !isSnapshot) // Delta의 경우 update 객체 진입
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    // 재귀 호출로 update 객체 내부의 bids/asks 파싱
                    ParseOrderBookLevel(ref reader, ref updates, ref count, ref u, ref pu, ref t, isSnapshot);
                }
            }
            else if (prop.SequenceEqual("bids"u8))
            {
                ParseSide(ref reader, Side.Buy, ref updates, ref count);
            }
            else if (prop.SequenceEqual("asks"u8))
            {
                ParseSide(ref reader, Side.Sell, ref updates, ref count);
            }
            else
            {
                // tt 등 기타 필드
                reader.TrySkip();
            }
        }
    }

    private void ParseSide(ref Utf8JsonReader reader, Side side, ref PriceLevelEntryArray updates, ref int count)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                // [Price, Size, Count]
                reader.Read();
                FastJsonParser.TryParseDecimal(ref reader, out decimal p); // Price

                reader.Read();
                FastJsonParser.TryParseDecimal(ref reader, out decimal q); // Size

                reader.Read(); // Count (Skip)
                reader.Read(); // End Array

                if (count < 40 && p > 0)
                {
                    updates[count++] = new PriceLevelEntry(side, p, q);
                }
            }
        }
    }

    private void ProcessTradeData(ref Utf8JsonReader reader, Instrument inst)
    {
        // data: [ { p:..., q:..., s:..., t:..., ... } ]
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) continue;

            decimal p = 0, q = 0;
            Side side = Side.Buy;
            long t = 0;
            long tradeId = 0;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var prop = reader.ValueSpan;
                reader.Read();

                if (prop.SequenceEqual("p"u8)) FastJsonParser.TryParseDecimal(ref reader, out p);
                else if (prop.SequenceEqual("q"u8)) FastJsonParser.TryParseDecimal(ref reader, out q);
                else if (prop.SequenceEqual("s"u8)) side = reader.ValueTextEquals("BUY"u8) ? Side.Buy : Side.Sell;
                else if (prop.SequenceEqual("t"u8)) t = reader.GetInt64();
                else if (prop.SequenceEqual("d"u8)) tradeId = reader.GetInt64(); // Trade ID
                else reader.TrySkip();
            }

            if (p > 0 && q > 0)
            {
                var updates = new PriceLevelEntryArray();
                updates[0] = new PriceLevelEntry(side, p, q);
                OnMarketDataReceived(new MarketDataEvent(tradeId, t, EventKind.Trade, inst.InstrumentId, SourceExchange, 0, CryptodotcomTopic.Trade.TopicId, 1, updates));
            }
        }
    }

    private void ProcessTickerData(ref Utf8JsonReader reader, Instrument inst)
    {
        // data: [ { i:..., t:..., a:..., b:..., ... } ]
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) continue;

            decimal bestBid = 0, bestAsk = 0, bidQty = 0, askQty = 0;
            long t = 0;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var prop = reader.ValueSpan;
                reader.Read();

                if (prop.SequenceEqual("b"u8)) FastJsonParser.TryParseDecimal(ref reader, out bestBid); // Bid Price
                else if (prop.SequenceEqual("bs"u8)) FastJsonParser.TryParseDecimal(ref reader, out bidQty); // Bid Size
                else if (prop.SequenceEqual("k"u8)) FastJsonParser.TryParseDecimal(ref reader, out bestAsk); // Ask Price
                else if (prop.SequenceEqual("ks"u8)) FastJsonParser.TryParseDecimal(ref reader, out askQty); // Ask Size
                else if (prop.SequenceEqual("t"u8)) t = reader.GetInt64();
                else reader.TrySkip();
            }

            // 데이터가 유효하다면 이벤트 전송
            if (bestBid > 0 || bestAsk > 0)
            {
                var updates = new PriceLevelEntryArray();
                int cnt = 0;
                if (bestBid > 0) updates[cnt++] = new PriceLevelEntry(Side.Buy, bestBid, bidQty); // 수량 없으면 1
                if (bestAsk > 0) updates[cnt++] = new PriceLevelEntry(Side.Sell, bestAsk, askQty);

                OnMarketDataReceived(new MarketDataEvent(0, t, EventKind.Snapshot, inst.InstrumentId, SourceExchange, 0, CryptodotcomTopic.Ticker.TopicId, cnt, updates));
            }
        }
    }

    // --- BaseFeedAdapter Abstract Implementations ---
    protected override string? GetPingMessage() => null; // Heartbeat handled by server request
    protected override bool IsPongMessage(ReadOnlySpan<byte> messageSpan) => false;
}