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

namespace OpenHFT.Feed.Adapters;

public class CoinonePublicAdapter : BaseFeedAdapter
{
    public override ExchangeEnum SourceExchange => ExchangeEnum.COINONE;
    protected override bool IsHeartbeatEnabled => false;
    private Task? _forcePingTask;
    private CancellationTokenSource? _pingCts;
    protected override string? GetPingMessage() => null;
    protected override bool IsPongMessage(ReadOnlySpan<byte> messageSpan)
    {
        return messageSpan.SequenceEqual("{\"response_type\":\"PONG\"}"u8);
    }
    protected override TimeSpan GetInactivityTimeout() => TimeSpan.FromSeconds(30);

    public CoinonePublicAdapter(ILogger<CoinonePublicAdapter> logger, ProductType type, IInstrumentRepository instrumentRepository, ExecutionMode executionMode)
        : base(logger, type, instrumentRepository, executionMode)
    {
        this.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (e.IsConnected)
        {
            // --- Start the ForcePingLoop when connected ---
            _logger.LogInformationWithCaller("Coinone public connection established. Starting ForcePingLoop.");

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
            _logger.LogInformationWithCaller("Coinone connection lost. Stopping ForcePingLoop.");
            _pingCts?.Cancel();
        }
    }

    protected override string GetBaseUrl()
    {
        return "wss://stream.coinone.co.kr";
    }

    protected override void ConfigureWebsocket(ClientWebSocket webSocket)
    {
        webSocket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
    }

    private async Task ForcePingLoop(CancellationToken token)
    {
        _logger.LogInformationWithCaller("CoinonePublicAdapter ForcePingLoop started.");
        try
        {
            while (!token.IsCancellationRequested && IsConnected)
            {
                // 30분 유휴 시 종료되므로 5분마다 PING 전송 (안전하게)
                await Task.Delay(TimeSpan.FromMinutes(5), token);

                if (IsConnected)
                {
                    // 코인원은 {"request_type": "PING"} JSON을 보내야 함
                    var pingJson = "{\"request_type\":\"PING\"}";
                    await SendMessageAsync(pingJson, token);
                    _logger.LogDebug("Sent PING to Coinone.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error in CoinonePublicAdapter ForcePingLoop.");
        }
    }

    protected override Task DoSubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        // 1. Create a list to hold all the individual subscription tasks.
        var subscriptionTasks = new List<Task>();

        _logger.LogInformationWithCaller($"Sending {subscriptions.Sum(kvp => kvp.Value.Count)} individual subscription requests to Coinone.");

        // 2. Iterate through each instrument and each topic to create a separate request for each.
        foreach (var (instrument, topics) in subscriptions)
        {
            foreach (var topic in topics)
            {
                // 3. Construct the JSON object for a single subscription.
                var request = new
                {
                    request_type = "SUBSCRIBE",
                    channel = topic.EventTypeString, // e.g., "ORDERBOOK", "TICKER", "TRADE"
                    topic = new
                    {
                        quote_currency = instrument.QuoteCurrency.ToString(),
                        target_currency = instrument.BaseCurrency.ToString()
                    }
                    // 'format' is optional, so we can omit it to use "DEFAULT".
                };

                var json = JsonSerializer.Serialize(request);

                _logger.LogInformationWithCaller($"Coinone Submitting: {json}");

                // 4. Add the task of sending this single message to our list.
                subscriptionTasks.Add(SendMessageAsync(json, cancellationToken));
            }
        }

        // 5. Return a single task that completes when all individual subscription tasks are done.
        return Task.WhenAll(subscriptionTasks);
    }

    protected override Task DoUnsubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // --- Parsing Logic ---
    protected override async Task ProcessMessage(ReadOnlyMemory<byte> messageBytes)
    {
        try
        {
            var reader = new Utf8JsonReader(messageBytes.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;

            ReadOnlySpan<byte> channel = default;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var propName = reader.ValueSpan;
                reader.Read();

                if (propName.SequenceEqual("response_type"u8))
                {
                    if (reader.ValueTextEquals("SUBSCRIBED"u8))
                    {
                        var logMsg = Encoding.UTF8.GetString(messageBytes.Span);
                        _logger.LogInformationWithCaller($"[Coinone] Subscription Confirmed: {logMsg}");
                        return;
                    }

                    if (reader.ValueTextEquals("PONG"u8) || reader.ValueTextEquals("CONNECTED"u8))
                    {
                        return;
                    }

                    // 에러 로그
                    if (reader.ValueTextEquals("ERROR"u8))
                    {
                        _logger.LogWarningWithCaller($"Coinone Error: {Encoding.UTF8.GetString(messageBytes.Span)}");
                        return;
                    }
                }
                else if (propName.SequenceEqual("channel"u8))
                {
                    channel = reader.ValueSpan;
                }
                else if (propName.SequenceEqual("data"u8))
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        if (channel.SequenceEqual("ORDERBOOK"u8)) ProcessOrderBookData(ref reader);
                        else if (channel.SequenceEqual("TICKER"u8)) ProcessTickerData(ref reader);
                        else if (channel.SequenceEqual("TRADE"u8)) ProcessTradeData(ref reader);
                        // else: Unknown channel
                    }
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
            _logger.LogErrorWithCaller(ex, "Coinone parsing failed.");
        }
    }

    private void ProcessOrderBookData(ref Utf8JsonReader reader)
    {
        string? targetCurr = null;
        string? quoteCurr = null;
        long ts = 0;
        var updates = new PriceLevelEntryArray();
        int count = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.ValueSpan;
            reader.Read();

            if (prop.SequenceEqual("target_currency"u8)) targetCurr = reader.GetString();
            else if (prop.SequenceEqual("quote_currency"u8)) quoteCurr = reader.GetString();
            else if (prop.SequenceEqual("timestamp"u8)) ts = reader.GetInt64();
            else if (prop.SequenceEqual("asks"u8)) ParseOrderBookSide(ref reader, Side.Sell, ref updates, ref count);
            else if (prop.SequenceEqual("bids"u8)) ParseOrderBookSide(ref reader, Side.Buy, ref updates, ref count);
            else reader.TrySkip();
        }

        if (targetCurr != null && count > 0)
        {
            var inst = _instrumentRepository.FindBySymbol($"{quoteCurr}-{targetCurr}", ProdType, SourceExchange);
            if (inst != null)
                OnMarketDataReceived(new MarketDataEvent(0, ts, EventKind.Snapshot, inst.InstrumentId, SourceExchange, 0, 0, count, updates));
        }
    }

    private void ParseOrderBookSide(ref Utf8JsonReader reader, Side side, ref PriceLevelEntryArray updates, ref int count)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                decimal p = 0, q = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var prop = reader.ValueSpan;
                    reader.Read();
                    if (prop.SequenceEqual("price"u8)) FastJsonParser.TryParseDecimal(ref reader, out p);
                    else if (prop.SequenceEqual("qty"u8)) FastJsonParser.TryParseDecimal(ref reader, out q);
                }
                if (count < 40 && p > 0) updates[count++] = new PriceLevelEntry(side, p, q);
            }
        }
    }

    private void ProcessTickerData(ref Utf8JsonReader reader)
    {
        string? targetCurr = null;
        string? quoteCurr = null;
        long ts = 0;
        decimal bestBid = 0, bestAsk = 0, bidQty = 0, askQty = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var prop = reader.ValueSpan;
            reader.Read();

            if (prop.SequenceEqual("target_currency"u8)) targetCurr = reader.GetString();
            else if (prop.SequenceEqual("quote_currency"u8)) quoteCurr = reader.GetString();
            else if (prop.SequenceEqual("timestamp"u8)) ts = reader.GetInt64();
            else if (prop.SequenceEqual("ask_best_price"u8)) FastJsonParser.TryParseDecimal(ref reader, out bestAsk);
            else if (prop.SequenceEqual("ask_best_qty"u8)) FastJsonParser.TryParseDecimal(ref reader, out askQty);
            else if (prop.SequenceEqual("bid_best_price"u8)) FastJsonParser.TryParseDecimal(ref reader, out bestBid);
            else if (prop.SequenceEqual("bid_best_qty"u8)) FastJsonParser.TryParseDecimal(ref reader, out bidQty);
            else reader.TrySkip();
        }

        if (targetCurr != null && (bestBid > 0 || bestAsk > 0))
        {
            var inst = _instrumentRepository.FindBySymbol($"{quoteCurr}-{targetCurr}", ProdType, SourceExchange);
            if (inst != null)
            {
                var updates = new PriceLevelEntryArray();
                int cnt = 0;
                if (bestBid > 0) updates[cnt++] = new PriceLevelEntry(Side.Buy, bestBid, bidQty);
                if (bestAsk > 0) updates[cnt++] = new PriceLevelEntry(Side.Sell, bestAsk, askQty);

                // Ticker는 BBO(Best Bid/Offer) Snapshot으로 처리
                OnMarketDataReceived(new MarketDataEvent(0, ts, EventKind.Snapshot, inst.InstrumentId, SourceExchange, 0, 0, cnt, updates));
            }
        }
    }

    private void ProcessTradeData(ref Utf8JsonReader reader)
    {
        string? targetCurr = null;
        string? quoteCurr = null;
        long ts = 0;
        decimal price = 0, qty = 0;
        bool isSellerMaker = false;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var prop = reader.ValueSpan;
            reader.Read();

            if (prop.SequenceEqual("target_currency"u8)) targetCurr = reader.GetString();
            else if (prop.SequenceEqual("quote_currency"u8)) quoteCurr = reader.GetString();
            else if (prop.SequenceEqual("timestamp"u8)) ts = reader.GetInt64();
            else if (prop.SequenceEqual("price"u8)) FastJsonParser.TryParseDecimal(ref reader, out price);
            else if (prop.SequenceEqual("qty"u8)) FastJsonParser.TryParseDecimal(ref reader, out qty);
            else if (prop.SequenceEqual("is_seller_maker"u8)) isSellerMaker = reader.GetBoolean();
            else reader.TrySkip();
        }

        if (targetCurr != null && price > 0)
        {
            var inst = _instrumentRepository.FindBySymbol($"{quoteCurr}-{targetCurr}", ProdType, SourceExchange);
            if (inst != null)
            {
                var updates = new PriceLevelEntryArray();
                var side = isSellerMaker ? Side.Buy : Side.Sell;

                updates[0] = new PriceLevelEntry(side, price, qty);
                OnMarketDataReceived(new MarketDataEvent(0, ts, EventKind.Trade, inst.InstrumentId, SourceExchange, 0, 0, 1, updates));
            }
        }
    }

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
