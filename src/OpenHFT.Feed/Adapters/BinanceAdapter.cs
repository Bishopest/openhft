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

    private void ProcessAggTradeRaw(ref Utf8JsonReader reader, int instrumentId)
    {
        decimal price = 0, qty = 0;
        long tradeTime = 0;
        bool isBuyerMaker = false;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("p"u8)) { reader.Read(); FastJsonParser.TryParseDecimal(ref reader, out price); }
                else if (reader.ValueTextEquals("q"u8)) { reader.Read(); FastJsonParser.TryParseDecimal(ref reader, out qty); }
                else if (reader.ValueTextEquals("T"u8)) { reader.Read(); tradeTime = reader.GetInt64(); }
                else if (reader.ValueTextEquals("m"u8)) { reader.Read(); isBuyerMaker = reader.GetBoolean(); }
            }
        }

        var updatesArray = new PriceLevelEntryArray();
        updatesArray[0] = new PriceLevelEntry(isBuyerMaker ? Side.Sell : Side.Buy, price, qty);

        OnMarketDataReceived(new MarketDataEvent(0, tradeTime, EventKind.Trade, instrumentId, SourceExchange, 0, BinanceTopic.AggTrade.TopicId, 1, updatesArray));
    }

    private void ProcessBookTickerRaw(ref Utf8JsonReader reader, int instrumentId)
    {
        decimal bPrice = 0, bQty = 0, aPrice = 0, aQty = 0;
        long eventTime = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("b"u8)) { reader.Read(); FastJsonParser.TryParseDecimal(ref reader, out bPrice); }
                else if (reader.ValueTextEquals("B"u8)) { reader.Read(); FastJsonParser.TryParseDecimal(ref reader, out bQty); }
                else if (reader.ValueTextEquals("a"u8)) { reader.Read(); FastJsonParser.TryParseDecimal(ref reader, out aPrice); }
                else if (reader.ValueTextEquals("A"u8)) { reader.Read(); FastJsonParser.TryParseDecimal(ref reader, out aQty); }
                else if (reader.ValueTextEquals("E"u8)) { reader.Read(); eventTime = reader.GetInt64(); }
            }
        }

        if (eventTime == 0) eventTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var updates = new PriceLevelEntryArray();
        updates[0] = new PriceLevelEntry(Side.Buy, bPrice, bQty);
        updates[1] = new PriceLevelEntry(Side.Sell, aPrice, aQty);

        OnMarketDataReceived(new MarketDataEvent(0, eventTime, EventKind.Snapshot, instrumentId, SourceExchange, 0, BinanceTopic.BookTicker.TopicId, 2, updates));
    }

    private void ProcessDepthUpdateRaw(ref Utf8JsonReader reader, int instrumentId, ReadOnlyMemory<byte> rawData)
    {
        if (_bookManagers.TryGetValue(instrumentId, out var bookManager))
        {
            // [중요] BookManager가 JsonElement에 의존하지 않도록 rawData 혹은 파싱된 구조체를 넘겨야 합니다.
            // 여기서는 성능을 위해 이미 파싱 루프가 시작된 reader 대신, 
            // 데이터 일관성을 위해 전체 ReadOnlyMemory를 넘기는 방식으로 설계합니다.
            bookManager.ProcessWsUpdate(rawData);
        }
    }

    private async Task HandleSubscriptionResponse()
    {
        foreach (var bookManager in _bookManagers.Values)
        {
            _ = bookManager.StartSyncAsync(CancellationToken.None);
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
            var reader = new Utf8JsonReader(messageBytes.Span);

            // 1. 초기 토큰 읽기 ({ 시작)
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;

            int instrumentId = -1;
            BinanceTopic? topic = null;
            bool isDataField = false;

            // 2. 스트리밍 파싱 루프
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    // A. 구독 응답 확인 (result 또는 id 필드가 있으면 무시)
                    if (reader.ValueTextEquals("result"u8) || reader.ValueTextEquals("id"u8))
                    {
                        await HandleSubscriptionResponse();
                        return;
                    }

                    // B. Combined Stream 처리 ("stream" 필드에서 정보 추출)
                    if (reader.ValueTextEquals("stream"u8))
                    {
                        reader.Read();
                        ParseStreamName(ref reader, out instrumentId, out topic);
                    }
                    // C. 실제 데이터 처리 ("data" 필드 진입)
                    else if (reader.ValueTextEquals("data"u8))
                    {
                        reader.Read(); // StartObject ({) 로 이동

                        if (topic == BinanceTopic.AggTrade) ProcessAggTradeRaw(ref reader, instrumentId);
                        else if (topic == BinanceTopic.BookTicker) ProcessBookTickerRaw(ref reader, instrumentId);
                        else if (topic == BinanceTopic.DepthUpdate) ProcessDepthUpdateRaw(ref reader, instrumentId, messageBytes);

                        break; // 데이터 처리가 끝났으므로 루프 종료
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OnError(new FeedErrorEventArgs(new FeedReceiveException(SourceExchange, "Zero-alloc parsing failed.", ex), null));
        }
    }

    private void ParseStreamName(ref Utf8JsonReader reader, out int instId, out BinanceTopic? topic)
    {
        instId = -1;
        topic = null;

        // streamName 예: "btcusdt@aggTrade"
        ReadOnlySpan<byte> streamSpan = reader.ValueSpan;
        int atIndex = streamSpan.IndexOf((byte)'@');
        if (atIndex <= 0) return;

        var symbolSpan = streamSpan.Slice(0, atIndex);
        var suffixSpan = streamSpan.Slice(atIndex);

        // 1. Symbol로 Instrument 찾기 (할당을 줄이기 위해 여기서만 GetString() 사용 혹은 캐시 사용)
        // 실제 운영 환경에서는 UTF8 Span을 직접 비교하는 Repository 확장이 가장 좋습니다.
        string symbol = Encoding.UTF8.GetString(symbolSpan);
        var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
        if (instrument != null) instId = instrument.InstrumentId;

        // 2. Suffix로 Topic 찾기 (바이트 비교)
        if (suffixSpan.SequenceEqual("@aggTrade"u8)) topic = BinanceTopic.AggTrade;
        else if (suffixSpan.SequenceEqual("@bookTicker"u8)) topic = BinanceTopic.BookTicker;
        else if (suffixSpan.SequenceEqual("@depth@100ms"u8) || suffixSpan.SequenceEqual("@depth"u8)) topic = BinanceTopic.DepthUpdate;
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
