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

namespace OpenHFT.Feed.Adapters;

/// <summary>
/// Binance WebSocket feed adapter for real-time market data
/// Supports both individual symbol streams and combined streams
/// </summary>
public class BinanceAdapter : BaseFeedAdapter
{
    private const string TopicAggTrade = "@aggTrade";
    private const string TopicBookTicker = "@bookTicker";
    private const string TopicDepth = "@depth20@100ms";
    private const string DefaultBaseUrl = "wss://fstream.binance.com/stream";

    public override ExchangeEnum SourceExchange => ExchangeEnum.BINANCE;

    public BinanceAdapter(ILogger<BinanceAdapter> logger, ProductType type, IInstrumentRepository instrumentRepository) : base(logger, type, instrumentRepository)
    {
    }

    private IEnumerable<string> GetDefaultStreamsForSymbol(string symbol)
    {
        var normalizedSymbol = symbol.ToLowerInvariant();
        yield return $"{normalizedSymbol}{TopicAggTrade}";
        yield return $"{normalizedSymbol}{TopicBookTicker}";
        yield return $"{normalizedSymbol}{TopicDepth}";
    }

    private void ProcessAggTrade(JsonElement data)
    {
        var symbol = data.GetProperty("s").GetString();
        if (symbol == null) return;
        var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
        if (instrument == null) return;

        // Price and quantity are sent as strings in the aggTrade stream.
        if (decimal.TryParse(data.GetProperty("p").GetString(), out var price) &&
            decimal.TryParse(data.GetProperty("q").GetString(), out var quantity))
        {
            var tradeTime = data.GetProperty("T").GetInt64(); // Milliseconds
            var isBuyerMaker = data.GetProperty("m").GetBoolean();

            // If buyer is the maker, the aggressor was a seller. Otherwise, a buyer.
            var side = isBuyerMaker ? Side.Sell : Side.Buy;

            var marketDataEvent = new MarketDataEvent(
                sequence: 0, // aggTrade doesn't have a clear sequence like depth updates
                timestamp: tradeTime * 1000, // Convert ms to microseconds
                side: side,
                priceTicks: PriceUtils.ToTicks(price), // Assumes a PriceUtils helper
                quantity: (long)(quantity * 100_000_000), // Convert to base units
                kind: EventKind.Trade,
                instrumentId: instrument.InstrumentId,
                exchange: SourceExchange
            );

            OnMarketDataReceived(marketDataEvent);
        }
        else
        {
            _logger.LogWarningWithCaller($"Failed to parse price or quantity for aggTrade on symbol {symbol}");
            return;

        }
    }

    private void ProcessBookTicker(JsonElement data)
    {
        var symbol = data.GetProperty("s").GetString();
        // if (symbol == null || !SymbolToInstrumentMap.TryGetValue(symbol, out var instrument)) return;
        if (symbol == null) return;
        var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
        if (instrument == null) return;


        var eventTime = data.GetProperty("E").GetInt64() * 1000; // Convert ms to microseconds

        // Process Best Bid
        if (decimal.TryParse(data.GetProperty("b").GetString(), out var bidPrice) &&
            decimal.TryParse(data.GetProperty("B").GetString(), out var bidQty) && bidQty > 0)
        {
            var bidEvent = new MarketDataEvent(
                sequence: 0,
                timestamp: eventTime,
                side: Side.Buy,
                priceTicks: PriceUtils.ToTicks(bidPrice),
                quantity: (long)(bidQty * 100_000_000),
                kind: EventKind.Update,
                instrumentId: instrument.InstrumentId,
                exchange: SourceExchange
            );
            OnMarketDataReceived(bidEvent);
        }

        // Process Best Ask
        if (decimal.TryParse(data.GetProperty("a").GetString(), out var askPrice) &&
            decimal.TryParse(data.GetProperty("A").GetString(), out var askQty) && askQty > 0)
        {
            var askEvent = new MarketDataEvent(
                sequence: 0,
                timestamp: eventTime,
                side: Side.Sell,
                priceTicks: PriceUtils.ToTicks(askPrice),
                quantity: (long)(askQty * 100_000_000),
                kind: EventKind.Update,
                instrumentId: instrument.InstrumentId,
                exchange: SourceExchange
            );
            OnMarketDataReceived(askEvent);
        }
    }

    private void ProcessDepthUpdate(JsonElement data)
    {
        var symbol = data.GetProperty("s").GetString();
        if (symbol == null) return;
        var instrument = _instrumentRepository.FindBySymbol(symbol, ProdType, SourceExchange);
        if (instrument == null) return;

        var timestamp = data.GetProperty("E").GetInt64() * 1000; // Event time in microseconds
        var finalUpdateId = data.GetProperty("u").GetInt64();
        var prevUpdateId = data.GetProperty("pu").GetInt64();


        // Here you should check sequence continuity using 'pu' and 'U'/'u' as per Binance docs.
        // For simplicity, this example just processes the updates.

        // Process bids
        if (data.TryGetProperty("b", out var bidsElement))
        {
            foreach (var bid in bidsElement.EnumerateArray())
            {
                if (decimal.TryParse(bid[0].GetString(), out var price) &&
                    decimal.TryParse(bid[1].GetString(), out var quantity))
                {
                    OnMarketDataReceived(new MarketDataEvent(
                        sequence: finalUpdateId,
                        timestamp: timestamp,
                        side: Side.Buy,
                        priceTicks: PriceUtils.ToTicks(price),
                        quantity: (long)(quantity * 100_000_000),
                        kind: quantity == 0 ? EventKind.Delete : EventKind.Update,
                        instrumentId: instrument.InstrumentId,
                        exchange: SourceExchange,
                        prevSequence: prevUpdateId
                    ));
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
                    OnMarketDataReceived(new MarketDataEvent(
                        sequence: finalUpdateId,
                        timestamp: timestamp,
                        side: Side.Sell,
                        priceTicks: PriceUtils.ToTicks(price),
                        quantity: (long)(quantity * 100_000_000),
                        kind: quantity == 0 ? EventKind.Delete : EventKind.Update,
                        instrumentId: instrument.InstrumentId,
                        exchange: SourceExchange
                    ));
                }
            }
        }
    }

    protected override string GetBaseUrl()
    {
        return DefaultBaseUrl;
    }

    protected override async Task ProcessMessage(MemoryStream messageStream)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(messageStream);
            var root = document.RootElement;

            // Subscription response, ignore it.
            if (root.TryGetProperty("result", out _) || root.TryGetProperty("id", out _))
            {
                return;
            }

            // Combined stream format: {"stream":"<streamName>","data":{...}}
            if (root.TryGetProperty("stream", out var streamElement) &&
                root.TryGetProperty("data", out var dataElement))
            {
                var streamName = streamElement.GetString() ?? string.Empty;
                var eventType = dataElement.TryGetProperty("e", out var et) ? et.GetString() : null;

                switch (eventType)
                {
                    case "aggTrade":
                        ProcessAggTrade(dataElement);
                        break;
                    case "bookTicker":
                        ProcessBookTicker(dataElement);
                        break;
                    case "depthUpdate":
                        ProcessDepthUpdate(dataElement);
                        break;
                }
            }
        }
        catch (JsonException jex)
        {
            // Handle parsing errors
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "JSON parsing failed.", GetRawMessage(messageStream), jex), null));
        }
        catch (Exception ex)
        {
            // Handle other processing errors
            OnError(new FeedErrorEventArgs(new FeedParseException(SourceExchange, "Failed to process message.", GetRawMessage(messageStream), ex), null));
        }
    }

    private string GetRawMessage(MemoryStream ms)
    {
        ms.Position = 0;
        using var reader = new StreamReader(ms, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    protected override Task DoSubscribeAsync(IEnumerable<Instrument> insts, CancellationToken cancellationToken)
    {
        var allStreams = insts
            .SelectMany(inst => GetDefaultStreamsForSymbol(inst.Symbol))
            .ToArray();

        if (!allStreams.Any())
        {
            return Task.CompletedTask;
        }

        var subscribeMessage = JsonSerializer.Serialize(new
        {
            method = "SUBSCRIBE",
            @params = allStreams,
            id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        _logger.LogInformationWithCaller($"Subscribing to {allStreams.Length} streams on {Exchange.Decode(SourceExchange)}");
        return SendMessageAsync(subscribeMessage, cancellationToken);
    }

    protected override Task DoUnsubscribeAsync(IEnumerable<Instrument> insts, CancellationToken cancellationToken)
    {
        var allStreams = insts
            .SelectMany(inst => GetDefaultStreamsForSymbol(inst.Symbol))
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
        websocket.Options.SetBuffer(8192, 8192); // 8KB buffersthrow new NotImplementedException();
    }
}
