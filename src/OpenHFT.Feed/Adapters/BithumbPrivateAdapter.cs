using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;
using OpenHFT.Gateway.ApiClient;

namespace OpenHFT.Feed.Adapters;

public class BithumbPrivateAdapter : BaseAuthFeedAdapter
{
    public override ExchangeEnum SourceExchange => ExchangeEnum.BITHUMB;
    public override StreamType StreamType => StreamType.PrivateStream;
    private string? _jwtToken;
    private readonly BithumbRestApiClient _apiClient;
    protected override bool IsHeartbeatEnabled => false;
    private Task? _forcePingTask;
    private CancellationTokenSource? _pingCts;

    public BithumbPrivateAdapter(
        ILogger<BithumbPrivateAdapter> logger,
        ProductType type,
        IInstrumentRepository instrumentRepository,
        ExecutionMode executionMode,
        BithumbRestApiClient apiClient,
        string? apiKey,
        string? apiSecret)
        : base(logger, type, instrumentRepository, executionMode)
    {
        _apiClient = apiClient;
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret))
        {
            base.SetApiKeyAndSecret(apiKey, apiSecret);
        }
        this.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (e.IsConnected)
        {
            // --- Start the ForcePingLoop when connected ---
            _logger.LogInformationWithCaller("Bithumb private connection established. Starting ForcePingLoop.");

            // Cancel any previous loop that might be running, just in case.
            _pingCts?.Cancel();
            _pingCts?.Dispose();

            // Create a new CancellationTokenSource for the ping loop.
            // It will be linked to the main adapter cancellation token.
            _pingCts = CancellationTokenSource.CreateLinkedTokenSource(GetAdapterCancellationToken());
            _forcePingTask = Task.Run(() => ForcePingLoop(_pingCts.Token), _pingCts.Token);
        }
        else
        {
            // --- Stop the ForcePingLoop when disconnected ---
            _logger.LogInformationWithCaller("Bithumb connection lost. Stopping ForcePingLoop.");
            _pingCts?.Cancel();
        }
    }

    protected override string GetBaseUrl()
    {
        return "wss://ws-api.bithumb.com/websocket/v1/private";
    }

    protected override void ConfigureWebsocket(ClientWebSocket webSocket)
    {
        // 0. api setting

        // 1. JWT에 들어갈 페이로드 데이터 구성 (사용자님이 주신 정보 기반)
        var payload = new
        {
            access_key = ApiKey,
            nonce = Guid.NewGuid().ToString(),
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // 2. [핵심] API Secret을 사용하여 실제로 서명하고 토큰 생성
        _jwtToken = CreateBithumbJwtToken(payload, ApiSecret!);

        _logger.LogInformationWithCaller("Bithumb JWT Token signed with Secret Key and prepared.");

        // 3. Bithumb 의 ping/pong 로직은 내장 옵션으로 구현 불가(invalidate)
        webSocket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        if (!string.IsNullOrEmpty(_jwtToken))
        {
            // 빗썸 Private 접속을 위한 JWT 인증 헤더 추가
            webSocket.Options.SetRequestHeader("Authorization", $"Bearer {_jwtToken}");
        }
    }

    private async Task ForcePingLoop(CancellationToken token)
    {
        _logger.LogInformationWithCaller("BithumbPrivateAdapter ForcePingLoop started.");
        try
        {
            // Send an initial ping immediately upon connection.
            if (IsConnected)
            {
                await SendMessageAsync("PING", token);
            }

            while (!token.IsCancellationRequested && IsConnected)
            {
                // Wait for 30 seconds before sending the next ping.
                await Task.Delay(TimeSpan.FromSeconds(30), token);

                if (IsConnected)
                {
                    _logger.LogDebug("BithumbPrivateAdapter Sending periodic PING to Bithumb.");
                    await SendMessageAsync("PING", token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformationWithCaller("BithumbPrivateAdapter ForcePingLoop was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error in BithumbPrivateAdapter ForcePingLoop.");
        }
        _logger.LogInformationWithCaller("BithumbPrivateAdapter ForcePingLoop stopped.");
    }

    protected override async Task DoAuthenticateAsync(CancellationToken cancellationToken)
    {
        OnAuthenticationStateChanged(true, "Authentication successful.");
        await Task.CompletedTask;
    }

    private string CreateBithumbJwtToken(object payloadObj, string secretKey)
    {
        // A. 헤더 정의 (HS256 알고리즘 사용)
        string headerJson = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
        string payloadJson = JsonSerializer.Serialize(payloadObj);

        // B. Header와 Payload를 Base64Url 인코딩
        string encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        string encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        // C. 서명 생성 대상 (header.payload)
        string stringToSign = $"{encodedHeader}.{encodedPayload}";

        // D. [중요] Secret Key로 HMAC-SHA256 서명 생성
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        byte[] signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        string encodedSignature = Base64UrlEncode(signatureBytes);

        // E. 최종 JWT 토큰 조합
        return $"{encodedHeader}.{encodedPayload}.{encodedSignature}";
    }

    /// <summary>
    /// JWT 표준에 맞는 Base64Url 인코딩 (일반 Base64에서 일부 문자 치환 및 패딩 제거)
    /// </summary>
    private string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }

    public override Task SubscribeToPrivateTopicsAsync(CancellationToken cancellationToken)
    {
        var privateTopics = BithumbTopic.GetAllPrivateTopics();
        var topicArgs = privateTopics.Select(t => t.GetStreamName("")).ToArray();
        if (!topicArgs.Any())
        {
            _logger.LogWarningWithCaller("No private topics defined for Bithumb; skipping subscription.");
            return Task.CompletedTask;
        }

        var sendTasks = new List<Task>();

        _logger.LogInformationWithCaller($"Subscribing to {topicArgs.Length} private Bithumb topics: {string.Join(", ", topicArgs)}");

        foreach (var topic in topicArgs)
        {
            var request = new object[]
            {
                new { ticket = Guid.NewGuid().ToString() },
                new { type = topic },
                new { format = "DEFAULT" }
            };

            var message = JsonSerializer.Serialize(request);

            sendTasks.Add(SendMessageAsync(message, cancellationToken));
        }

        return Task.WhenAll(sendTasks);
    }

    protected override Task DoSubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller($"BithumbPrivateAdapter can not subscribe: {subscriptions.Values}");
        return Task.CompletedTask;
    }

    protected override Task DoUnsubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller($"BithumbPrivateAdapter can not unsubscribe: {subscriptions.Values}");
        return Task.CompletedTask;
    }

    protected override async Task ProcessMessage(ReadOnlyMemory<byte> messageBytes)
    {
        try
        {
            var reader = new Utf8JsonReader(messageBytes.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;

            // Find the "type" property to identify the message.
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                var propName = reader.ValueSpan;
                if (!reader.Read()) break; // Move to value

                if (propName.SequenceEqual("type"u8))
                {
                    if (reader.ValueTextEquals("myOrder"u8))
                    {
                        // Found the myOrder message, process it.
                        ProcessMyOrderRaw(ref reader);
                    }
                    // Since one message contains one type, we can break after processing.
                    break;
                }
                else
                {
                    // Skip other top-level properties like "status", "resmsg", etc.
                    reader.TrySkip();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bithumb Private parsing failed.");
        }
    }

    private void ProcessMyOrderRaw(ref Utf8JsonReader reader)
    {
        // This logic is copied and adapted from the Public adapter.
        string? orderId = null, tradeId = null, symbol = null, state = null;
        decimal price = 0, volume = 0, leavesQty = 0, lastQty = 0;
        Side side = Side.Buy;
        long ts = 0;

        // The reader is currently at the value of the "type" property.
        // We need to continue reading properties of the same object.
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.ValueSpan;
            if (!reader.Read()) break;

            if (prop.SequenceEqual("uuid"u8)) orderId = reader.GetString();
            else if (prop.SequenceEqual("trade_uuid"u8)) tradeId = reader.GetString();
            else if (prop.SequenceEqual("code"u8)) symbol = reader.GetString();
            else if (prop.SequenceEqual("state"u8)) state = reader.GetString();
            else if (prop.SequenceEqual("ask_bid"u8)) side = reader.ValueTextEquals("ASK"u8) ? Side.Sell : Side.Buy;
            else if (prop.SequenceEqual("price"u8)) FastJsonParser.TryParseDecimal(ref reader, out price);
            else if (prop.SequenceEqual("volume"u8)) FastJsonParser.TryParseDecimal(ref reader, out volume);
            else if (prop.SequenceEqual("executed_volume"u8)) FastJsonParser.TryParseDecimal(ref reader, out lastQty);
            else if (prop.SequenceEqual("remaining_volume"u8)) FastJsonParser.TryParseDecimal(ref reader, out leavesQty);
            else if (prop.SequenceEqual("timestamp"u8)) ts = reader.GetInt64();
            else reader.TrySkip(); // Skip properties we don't need
        }

        var inst = _instrumentRepository.FindBySymbol(symbol ?? "", ProdType, SourceExchange);
        if (inst != null && !string.IsNullOrEmpty(orderId))
        {
            // ClientOrderId is not provided by Bithumb private stream, so we must find it
            // via the OrderRouter using the exchange-provided UUID.
            // This is a more complex lookup. For now, we set it to 0.
            var report = new OrderStatusReport(
                clientOrderId: 0, // We cannot know the ClientOrderId from this message alone.
                exchangeOrderId: orderId,
                executionId: tradeId,
                instrumentId: inst.InstrumentId,
                side: side,
                status: MapBithumbStatus(state),
                price: Price.FromDecimal(price),
                quantity: Quantity.FromDecimal(volume),
                lastQuantity: Quantity.FromDecimal(lastQty),
                lastPrice: Price.FromDecimal(price),
                leavesQuantity: Quantity.FromDecimal(leavesQty),
                timestamp: ts
            );
            OnOrderUpdateReceived(report);
        }
    }

    private OrderStatus MapBithumbStatus(string? state) => state switch
    {
        "wait" => OrderStatus.New,
        "trade" => OrderStatus.PartiallyFilled,
        "done" => OrderStatus.Filled,
        "cancel" => OrderStatus.Cancelled,
        _ => OrderStatus.Pending
    };

    protected override string? GetPingMessage() => null;
    protected override bool IsPongMessage(ReadOnlySpan<byte> messageSpan)
    {
        return false;
    }
    protected override TimeSpan GetInactivityTimeout() => TimeSpan.FromSeconds(30);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pingCts?.Cancel();
            _pingCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}