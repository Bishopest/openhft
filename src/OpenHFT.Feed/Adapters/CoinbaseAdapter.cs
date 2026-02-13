using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Models;
using Jose;

namespace OpenHFT.Feed.Adapters;

public class CoinbaseAdapter : BaseFeedAdapter
{
    public override ExchangeEnum SourceExchange => ExchangeEnum.COINBASE;
    // Coinbase sends explicit heartbeats if subscribed, but we can rely on data flow.
    protected override bool IsHeartbeatEnabled => false;

    private readonly ConcurrentDictionary<int, CoinbaseBookManager> _bookManagers = new();
    private readonly ConcurrentQueue<IDictionary<Instrument, List<ExchangeTopic>>> _pendingSubscriptions = new();
    private volatile bool _isAuthenticated = false;
    private long _lastGlobalSequence = -1;

    public CoinbaseAdapter(ILogger<CoinbaseAdapter> logger, ProductType type, IInstrumentRepository instrumentRepository, ExecutionMode executionMode)
        : base(logger, type, instrumentRepository, executionMode)
    {
    }

    protected override string GetBaseUrl()
    {
        // Coinbase Market Data Endpoint
        return _executionMode switch
        {
            ExecutionMode.Live => "wss://advanced-trade-ws.coinbase.com",
            ExecutionMode.Testnet => "wss://ws-feed-public.sandbox.exchange.coinbase.com",
            _ => throw new ArgumentOutOfRangeException(nameof(_executionMode))
        };
    }

    protected override void ConfigureWebsocket(ClientWebSocket webSocket)
    {
        // Compression (RFC7692)
        // .NET ClientWebSocket supports compression automatically on some platforms/versions, 
        // but explicit header might be needed.
        // webSocket.Options.SetRequestHeader("Sec-WebSocket-Extensions", "permessage-deflate");

        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    // --- Subscription ---
    protected override async Task DoSubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        var authRequiredSubs = new Dictionary<Instrument, List<ExchangeTopic>>();
        var publicSubs = new Dictionary<Instrument, List<ExchangeTopic>>();

        // 1. [핵심] 구독 요청을 인증 필요 여부에 따라 분리
        foreach (var (instrument, topics) in subscriptions)
        {
            foreach (var topic in topics)
            {
                if (!publicSubs.ContainsKey(instrument))
                    publicSubs[instrument] = new List<ExchangeTopic>();
                publicSubs[instrument].Add(topic);
            }
        }

        // 2. 인증이 필요 없는 토픽은 즉시 전송
        if (publicSubs.Any())
        {
            _logger.LogInformationWithCaller("Sending Coinbase public subscription request immediately.");
            await SendSubscriptionRequest(publicSubs, false, cancellationToken);
        }

        // 3. 인증이 필요한 토픽 처리
        if (authRequiredSubs.Any())
        {
            if (_isAuthenticated)
            {
                // 이미 인증이 완료되었다면 바로 전송
                _logger.LogInformationWithCaller("Coinbase Authentication is ready. Sending authenticated subscription request.");
                await SendSubscriptionRequest(authRequiredSubs, false, cancellationToken);
            }
            else
            {
                // 아직 인증 전이라면 큐에 저장
                _logger.LogInformationWithCaller("Coinbase Authentication required but not ready. Queuing authenticated subscription request.");
                _pendingSubscriptions.Enqueue(authRequiredSubs);
            }
        }
    }

    // [핵심 3] 실제 구독 메시지를 생성하고 전송하는 로직 (기존 DoSubscribeAsync 내용)
    private async Task SendSubscriptionRequest(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, bool requiresAuth, CancellationToken cancellationToken)
    {
        // Advanced Trade는 채널별로 메시지를 구성하는 것이 안정적입니다.
        var productIds = subscriptions.Keys.Select(k => k.Symbol).Distinct().ToArray();
        var groupedTopics = subscriptions.Values.SelectMany(v => v).DistinctBy(t => t.GetTopicName());

        foreach (var topic in groupedTopics)
        {
            var requestPayload = new Dictionary<string, object>
            {
                { "type", "subscribe" },
                { "product_ids", productIds },
                { "channel", topic.GetTopicName() }
            };

            if (requiresAuth)
            {
                // 생성된 JWT를 'jwt' 필드에 추가
                requestPayload["jwt"] = GenerateAdvancedTradeJwt();
            }

            var json = JsonSerializer.Serialize(requestPayload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            _logger.LogInformationWithCaller($"Coinbase Advanced Trade Submitting: {topic.GetTopicName()} for {productIds.Length} products (Auth: {requiresAuth})");
            await SendMessageAsync(json, cancellationToken);
        }
    }

    //TO-DO
    private string GenerateAdvancedTradeJwt()
    {
        return "";
    }


    protected override Task DoUnsubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        // 구독 취소 로직 (유사함)
        return Task.CompletedTask;
    }

    // --- Parsing ---

    protected override async Task ProcessMessage(ReadOnlyMemory<byte> messageBytes)
    {
        var reader = new Utf8JsonReader(messageBytes.Span);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return;

        string? channel = null;
        long sequence = 0;
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var prop = reader.ValueSpan;
            reader.Read();

            if (prop.SequenceEqual("channel"u8))
            {
                channel = reader.GetString();
            }
            else if (prop.SequenceEqual("sequence_num"u8))
            {
                sequence = reader.GetInt64();
                if (_lastGlobalSequence != -1 && sequence <= _lastGlobalSequence)
                {
                    _logger.LogWarningWithCaller($"Sequence gap derected. Last ={_lastGlobalSequence}, Current = {sequence}");
                    return;
                }

                _lastGlobalSequence = sequence;
            }
            else if (prop.SequenceEqual("timestamp"u8))
            {
                if (reader.TokenType == JsonTokenType.String &&
                    DateTimeOffset.TryParse(reader.GetString(), out var dt))
                {
                    timestamp = dt.ToUnixTimeMilliseconds();
                }
            }
            else if (prop.SequenceEqual("events"u8))
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.TrySkip();
                    continue;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.TrySkip();
                        continue;
                    }

                    string? eventType = null;
                    string? productId = null;

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName)
                            continue;

                        var eProp = reader.ValueSpan;
                        reader.Read();

                        // -----------------------------------
                        // EVENT TYPE
                        // -----------------------------------
                        if (eProp.SequenceEqual("type"u8))
                        {
                            eventType = reader.GetString();
                        }

                        // ===================================
                        // L2_DATA CHANNEL
                        // ===================================
                        else if (channel == "l2_data" &&
                                 eProp.SequenceEqual("product_id"u8))
                        {
                            productId = reader.GetString();
                        }
                        else if (channel == "l2_data" &&
                                 eProp.SequenceEqual("updates"u8))
                        {
                            if (productId == null || eventType == null)
                            {
                                reader.TrySkip();
                                continue;
                            }

                            var inst = _instrumentRepository
                                .FindBySymbol(productId, ProdType, SourceExchange);

                            if (inst == null)
                            {
                                reader.TrySkip();
                                continue;
                            }

                            var manager = GetBookManager(inst.InstrumentId);

                            manager.ParseL2UpdatesAndDispatch(
                                ref reader,
                                eventType,
                                timestamp
                            );
                        }

                        // ===================================
                        // TICKER CHANNEL
                        // ===================================
                        else if (channel == "ticker" &&
                                 eProp.SequenceEqual("tickers"u8))
                        {
                            if (reader.TokenType != JsonTokenType.StartArray)
                            {
                                reader.TrySkip();
                                continue;
                            }

                            while (reader.Read() &&
                                   reader.TokenType != JsonTokenType.EndArray)
                            {
                                if (reader.TokenType != JsonTokenType.StartObject)
                                {
                                    reader.TrySkip();
                                    continue;
                                }

                                string? tProductId = null;
                                decimal bestBid = 0;
                                decimal bestBidQty = 0;
                                decimal bestAsk = 0;
                                decimal bestAskQty = 0;

                                while (reader.Read() &&
                                       reader.TokenType != JsonTokenType.EndObject)
                                {
                                    if (reader.TokenType != JsonTokenType.PropertyName)
                                        continue;

                                    var tProp = reader.ValueSpan;
                                    reader.Read();

                                    if (tProp.SequenceEqual("product_id"u8))
                                    {
                                        tProductId = reader.GetString();
                                    }
                                    else if (tProp.SequenceEqual("best_bid"u8))
                                    {
                                        FastJsonParser.TryParseDecimal(ref reader, out bestBid);
                                    }
                                    else if (tProp.SequenceEqual("best_bid_quantity"u8))
                                    {
                                        FastJsonParser.TryParseDecimal(ref reader, out bestBidQty);
                                    }
                                    else if (tProp.SequenceEqual("best_ask"u8))
                                    {
                                        FastJsonParser.TryParseDecimal(ref reader, out bestAsk);
                                    }
                                    else if (tProp.SequenceEqual("best_ask_quantity"u8))
                                    {
                                        FastJsonParser.TryParseDecimal(ref reader, out bestAskQty);
                                    }
                                    else
                                    {
                                        reader.TrySkip();
                                    }
                                }

                                if (tProductId == null)
                                    continue;

                                var inst = _instrumentRepository
                                    .FindBySymbol(tProductId, ProdType, SourceExchange);

                                if (inst == null)
                                    continue;

                                // ticker를 trade event로 dispatch
                                var bidEntry = new PriceLevelEntry(
                                    Side.Buy,
                                    bestBid,
                                    bestBidQty);
                                var askEntry = new PriceLevelEntry(
                                    Side.Sell,
                                    bestAsk,
                                    bestAskQty);

                                var arr = new PriceLevelEntryArray();
                                arr[0] = bidEntry;
                                arr[1] = askEntry;

                                OnMarketDataReceived(new MarketDataEvent(
                                    0,
                                    timestamp,
                                    EventKind.Snapshot,
                                    inst.InstrumentId,
                                    ExchangeEnum.COINBASE,
                                    0,
                                    CoinbaseTopic.Ticker.TopicId,
                                    2,
                                    arr,
                                    true
                                ));
                            }
                        }

                        else
                        {
                            reader.TrySkip();
                        }
                    }
                }
            }
            else
            {
                reader.TrySkip();
            }
        }
    }

    private CoinbaseBookManager GetBookManager(int instrumentId)
    {
        return _bookManagers.GetOrAdd(instrumentId, _ => new CoinbaseBookManager(_logger, instrumentId, OnMarketDataReceived));
    }

    protected override string? GetPingMessage() => null;
    protected override bool IsPongMessage(ReadOnlySpan<byte> messageSpan) => false;
}