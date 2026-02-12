using System;
using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Models;

namespace OpenHFT.Feed.Adapters;

public class CryptodotcomBookManager
{
    private readonly ILogger _logger;
    private readonly int _instrumentId;
    private readonly Action<MarketDataEvent> _eventDispatcher;
    private readonly Action _resubscribeAction; // 재구독 요청 콜백

    private long _lastUpdateId = -1;
    private bool _isSynced = false;
    private static readonly ArrayPool<PriceLevelEntry> _entryPool = ArrayPool<PriceLevelEntry>.Shared;

    public CryptodotcomBookManager(
        ILogger logger,
        int instrumentId,
        Action<MarketDataEvent> eventDispatcher,
        Action resubscribeAction)
    {
        _logger = logger;
        _instrumentId = instrumentId;
        _eventDispatcher = eventDispatcher;
        _resubscribeAction = resubscribeAction;
    }

    public void ProcessWsUpdate(ref Utf8JsonReader reader, bool isSnapshot)
    {
        // 1. 메타데이터 파싱
        long u = 0, pu = 0, t = 0;
        int count = 0;
        var entries = _entryPool.Rent(256); // 넉넉하게 대여

        try
        {
            // 객체 내부 순회 (u, pu, t, bids, asks, update 등)
            // 주의: Crypto.com 델타는 "update" 객체 안에 bids/asks가 있을 수 있음
            ParseLevelRecursive(ref reader, ref entries, ref count, ref u, ref pu, ref t, isSnapshot);

            if (count == 0)
            {
                _lastUpdateId = u;
                return;
            }
            // 2. 시퀀스 검증 및 상태 관리
            if (isSnapshot)
            {
                // 스냅샷 수신: 무조건 적용 및 초기화
                _lastUpdateId = u;
                _isSynced = true;
                _logger.LogInformationWithCaller($"Snapshot received for {_instrumentId}. LastUpdateId: {u}");

                DispatchEvent(u, t, EventKind.Snapshot, 0, count, entries);
            }
            else
            {
                if (!_isSynced)
                {
                    return;
                }

                if (pu != _lastUpdateId)
                {
                    _logger.LogWarningWithCaller($"[{_instrumentId}] GAP Detected! LastU: {_lastUpdateId} != CurrentPU: {pu}. Triggering Resync.");

                    // 동기화 해제 및 재구독 요청
                    _isSynced = false;
                    _lastUpdateId = -1;

                    // Adapter에게 재구독 명령 (비동기로 실행하여 수신 루프 차단 방지)
                    Task.Run(() => _resubscribeAction());
                    return;
                }

                _lastUpdateId = u;
                DispatchEvent(u, t, EventKind.Update, pu, count, entries);
            }
        }
        finally
        {
            _entryPool.Return(entries);
        }
    }

    private void ParseLevelRecursive(ref Utf8JsonReader reader, ref PriceLevelEntry[] entries, ref int count, ref long u, ref long pu, ref long t, bool isSnapshot)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.ValueSpan;
            reader.Read();

            if (prop.SequenceEqual("u"u8)) u = reader.GetInt64();
            else if (prop.SequenceEqual("pu"u8)) pu = reader.GetInt64();
            else if (prop.SequenceEqual("t"u8)) t = reader.GetInt64();
            else if (prop.SequenceEqual("update"u8) && !isSnapshot)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                    ParseLevelRecursive(ref reader, ref entries, ref count, ref u, ref pu, ref t, isSnapshot);
            }
            else if (prop.SequenceEqual("bids"u8)) ParseSide(ref reader, Side.Buy, ref entries, ref count);
            else if (prop.SequenceEqual("asks"u8)) ParseSide(ref reader, Side.Sell, ref entries, ref count);
            else reader.TrySkip();
        }
    }

    private void ParseSide(ref Utf8JsonReader reader, Side side, ref PriceLevelEntry[] entries, ref int count)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                reader.Read(); FastJsonParser.TryParseDecimal(ref reader, out decimal p);
                reader.Read(); FastJsonParser.TryParseDecimal(ref reader, out decimal q);
                reader.Read(); // Order Count
                reader.Read(); // End Array

                // 버퍼 확장
                if (count >= entries.Length)
                {
                    var newBuffer = _entryPool.Rent(entries.Length * 2);
                    Array.Copy(entries, newBuffer, entries.Length);
                    _entryPool.Return(entries);
                    entries = newBuffer;
                }

                if (p > 0) entries[count++] = new PriceLevelEntry(side, p, q);
            }
        }
    }

    private void DispatchEvent(long u, long t, EventKind kind, long pu, int count, PriceLevelEntry[] entries)
    {
        // 40개씩 끊어서 전송
        for (int i = 0; i < count; i += 40)
        {
            var chunk = new PriceLevelEntryArray();
            int chunkSize = Math.Min(40, count - i);
            for (int j = 0; j < chunkSize; j++) chunk[j] = entries[i + j];

            _eventDispatcher(new MarketDataEvent(
                u, t, kind, _instrumentId, ExchangeEnum.CRYPTODOTCOM,
                pu, CryptodotcomTopic.OrderBook.TopicId, chunkSize, chunk, (i + 40) >= count
            ));
        }
    }
}
