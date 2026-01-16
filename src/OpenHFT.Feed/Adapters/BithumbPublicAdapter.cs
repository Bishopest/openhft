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

public class BithumbPublicAdapter : BaseFeedAdapter
{
    public override ExchangeEnum SourceExchange => ExchangeEnum.BITHUMB;
    protected override bool IsHeartbeatEnabled => false;
    private Task? _forcePingTask;
    private CancellationTokenSource? _pingCts;

    public BithumbPublicAdapter(ILogger<BithumbPublicAdapter> logger, ProductType type, IInstrumentRepository instrumentRepository, ExecutionMode executionMode)
        : base(logger, type, instrumentRepository, executionMode)
    {
        this.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (e.IsConnected)
        {
            // --- Start the ForcePingLoop when connected ---
            _logger.LogInformationWithCaller("Bithumb public connection established. Starting ForcePingLoop.");

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
        return "wss://ws-api.bithumb.com/websocket/v1";
    }

    protected override void ConfigureWebsocket(ClientWebSocket webSocket)
    {
        webSocket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
    }

    private async Task ForcePingLoop(CancellationToken token)
    {
        _logger.LogInformationWithCaller("BithumbPublicAdapter ForcePingLoop started.");
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
                    _logger.LogDebug("BithumbPublicAdapter Sending periodic PING to Bithumb.");
                    await SendMessageAsync("PING", token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformationWithCaller("BithumbPublicAdapter ForcePingLoop was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error in BithumbPublicAdapter ForcePingLoop.");
        }
        _logger.LogInformationWithCaller("BithumbPublicAdapter ForcePingLoop stopped.");
    }

    protected override Task DoSubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        // Bithumb overwrites subscriptions on each request.
        // Therefore, we must send the ENTIRE list of current subscriptions every time.
        // Get the full list of subscriptions from the base class.
        // This requires access to the _subscriptions field. Let's add a helper in the base class.
        var allCurrentSubscriptions = base.GetCurrentSubscriptions();

        if (!allCurrentSubscriptions.Any())
        {
            _logger.LogWarningWithCaller("No subscriptions to send for Bithumb.");
            return Task.CompletedTask;
        }

        var requestArray = new List<object>
        {
            new { ticket = Guid.NewGuid().ToString() }
        };

        var topics = allCurrentSubscriptions
            .SelectMany(kvp => kvp.Value.Select(topicId => (kvp.Key.Symbol, TopicId: topicId)))
            .Select(t =>
            {
                TopicRegistry.TryGetTopic(t.TopicId, out var topic);
                return (t.Symbol, Topic: topic);
            })
            .Where(t => t.Topic != null)
            .GroupBy(x => x.Topic!.EventTypeString);

        foreach (var group in topics)
        {
            requestArray.Add(new
            {
                type = group.Key,
                codes = group.Select(x => x.Symbol).Distinct().ToArray()
            });
        }

        requestArray.Add(new { format = "DEFAULT" });

        var json = JsonSerializer.Serialize(requestArray, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        _logger.LogInformationWithCaller($"Bithumb re-submitting all topics: {json}");
        return SendMessageAsync(json, cancellationToken);
    }

    protected override Task DoUnsubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected override async Task ProcessMessage(ReadOnlyMemory<byte> messageBytes)
    {
        try
        {
            var reader = new Utf8JsonReader(messageBytes.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var propName = reader.ValueSpan;
                reader.Read();

                if (propName.SequenceEqual("type"u8))
                {
                    if (reader.ValueTextEquals("orderbook"u8)) ProcessOrderBook_Raw(ref reader);
                    else if (reader.ValueTextEquals("trade"u8)) ProcessTrade_Raw(ref reader);
                    break;
                }
                else if (propName.SequenceEqual("status"u8))
                {
                    // {"status":"UP"} 메시지가 ReceiveLoop에서 Pong으로 걸러지지 않고 여기까지 왔을 경우 처리
                    return;
                }
                else
                {
                    reader.TrySkip();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bithumb parsing failed.");
        }
    }

    private void ProcessOrderBook_Raw(ref Utf8JsonReader reader)
    {
        string? symbol = null;
        long ts = 0;
        var updates = new PriceLevelEntryArray();
        int count = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var prop = reader.ValueSpan;
            reader.Read();

            if (prop.SequenceEqual("code"u8)) symbol = reader.GetString();
            else if (prop.SequenceEqual("timestamp"u8)) ts = reader.GetInt64();
            else if (prop.SequenceEqual("orderbook_units"u8))
            {
                if (reader.TokenType != JsonTokenType.StartArray) continue;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    decimal ap = 0, @as = 0, bp = 0, bs = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        var unitProp = reader.ValueSpan;
                        reader.Read();
                        if (unitProp.SequenceEqual("ask_price"u8)) FastJsonParser.TryParseDecimal(ref reader, out ap);
                        else if (unitProp.SequenceEqual("ask_size"u8)) FastJsonParser.TryParseDecimal(ref reader, out @as);
                        else if (unitProp.SequenceEqual("bid_price"u8)) FastJsonParser.TryParseDecimal(ref reader, out bp);
                        else if (unitProp.SequenceEqual("bid_size"u8)) FastJsonParser.TryParseDecimal(ref reader, out bs);
                    }
                    if (count < 40)
                    {
                        if (ap > 0) updates[count++] = new PriceLevelEntry(Side.Sell, ap, @as);
                        if (bp > 0 && count < 40) updates[count++] = new PriceLevelEntry(Side.Buy, bp, bs);
                    }
                }
            }
        }

        if (symbol != null && count > 0)
        {
            var inst = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
            if (inst != null)
                OnMarketDataReceived(new MarketDataEvent(0, ts, EventKind.Snapshot, inst.InstrumentId, SourceExchange, 0, BithumbTopic.OrderBook.TopicId, count, updates));
        }
    }

    private void ProcessTrade_Raw(ref Utf8JsonReader reader)
    {
        decimal price = 0, qty = 0;
        Side side = Side.Buy;
        string? symbol = null;
        long ts = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var prop = reader.ValueSpan;
            reader.Read();

            if (prop.SequenceEqual("code"u8)) symbol = reader.GetString();
            else if (prop.SequenceEqual("trade_price"u8)) FastJsonParser.TryParseDecimal(ref reader, out price);
            else if (prop.SequenceEqual("trade_volume"u8)) FastJsonParser.TryParseDecimal(ref reader, out qty);
            else if (prop.SequenceEqual("ask_bid"u8)) side = reader.ValueTextEquals("ASK"u8) ? Side.Sell : Side.Buy;
            else if (prop.SequenceEqual("trade_timestamp"u8)) ts = reader.GetInt64();
        }

        if (symbol != null)
        {
            var inst = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
            if (inst != null)
            {
                var updates = new PriceLevelEntryArray();
                updates[0] = new PriceLevelEntry(side, price, qty);
                OnMarketDataReceived(new MarketDataEvent(0, ts, EventKind.Trade, inst.InstrumentId, SourceExchange, 0, BithumbTopic.Trade.TopicId, 1, updates));
            }
        }
    }

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