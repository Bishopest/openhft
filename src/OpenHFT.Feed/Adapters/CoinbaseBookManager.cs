using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Models;

namespace OpenHFT.Feed.Adapters;

public class CoinbaseBookManager
{
    private readonly ILogger _logger;
    private readonly int _instrumentId;
    private readonly Action<MarketDataEvent> _eventDispatcher;

    private bool _isSynced = false;

    private static readonly ArrayPool<PriceLevelEntry> _entryPool = ArrayPool<PriceLevelEntry>.Shared;

    public CoinbaseBookManager(ILogger logger, int instrumentId, Action<MarketDataEvent> eventDispatcher)
    {
        _logger = logger;
        _instrumentId = instrumentId;
        _eventDispatcher = eventDispatcher;
    }

    public void Reset()
    {
        _isSynced = false;
    }

    // =========================================================
    // ADVANCED TRADE L2_DATA PARSER
    // =========================================================
    public void ParseL2UpdatesAndDispatch(
        ref Utf8JsonReader reader,
        string eventType,
        long timestamp)
    {
        int count = 0;
        var entries = _entryPool.Rent(256);

        try
        {
            // ----------------------------------------
            // SNAPSHOT
            // ----------------------------------------
            if (eventType == "snapshot")
            {
                _isSynced = true;
            }
            else
            {
                // sync 전 update drop
                if (!_isSynced)
                {
                    reader.TrySkip();
                    return;
                }
            }

            // ----------------------------------------
            // updates 배열 순회
            // ----------------------------------------
            while (reader.Read() &&
                   reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.TrySkip();
                    continue;
                }

                Side side = Side.Buy;
                decimal price = 0;
                decimal size = 0;

                while (reader.Read() &&
                       reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        continue;

                    var prop = reader.ValueSpan;
                    reader.Read();

                    if (prop.SequenceEqual("side"u8))
                    {
                        side = reader.ValueTextEquals("bid"u8)
                            ? Side.Buy
                            : Side.Sell;
                    }
                    else if (prop.SequenceEqual("price_level"u8))
                    {
                        FastJsonParser.TryParseDecimal(ref reader, out price);
                    }
                    else if (prop.SequenceEqual("new_quantity"u8))
                    {
                        FastJsonParser.TryParseDecimal(ref reader, out size);
                    }
                    else
                    {
                        reader.TrySkip();
                    }
                }

                if (count >= entries.Length)
                    ResizeBuffer(ref entries);

                entries[count++] =
                    new PriceLevelEntry(side, price, size);
            }

            if (count > 0)
            {
                var kind = eventType == "snapshot"
                    ? EventKind.Snapshot
                    : EventKind.Update;

                DispatchEvent(
                    0,
                    timestamp,
                    kind,
                    0,
                    count,
                    entries);
            }
        }
        finally
        {
            _entryPool.Return(entries);
        }
    }

    // =========================================================
    // INTERNAL DISPATCH
    // =========================================================
    private void DispatchEvent(
        long u,
        long t,
        EventKind kind,
        long pu,
        int count,
        PriceLevelEntry[] entries)
    {
        for (int i = 0; i < count; i += 40)
        {
            var chunk = new PriceLevelEntryArray();
            int chunkSize = Math.Min(40, count - i);

            for (int j = 0; j < chunkSize; j++)
                chunk[j] = entries[i + j];

            var eventKind = kind;
            if (eventKind == EventKind.Snapshot && i != 0) eventKind = EventKind.Update;

            _eventDispatcher(new MarketDataEvent(
                u,
                t,
                eventKind,
                _instrumentId,
                ExchangeEnum.COINBASE,
                pu,
                CoinbaseTopic.OrderBook.TopicId,
                chunkSize,
                chunk,
                (i + 40) >= count
            ));
        }
    }

    private void ResizeBuffer(ref PriceLevelEntry[] entries)
    {
        var newBuffer = _entryPool.Rent(entries.Length * 2);
        Array.Copy(entries, newBuffer, entries.Length);
        _entryPool.Return(entries);
        entries = newBuffer;
    }
}