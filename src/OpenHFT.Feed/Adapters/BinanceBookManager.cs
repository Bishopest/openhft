using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Models;
using OpenHFT.Gateway.ApiClient;

namespace OpenHFT.Feed.Adapters;

/// <summary>
/// Manages the state for a single instrument's order book synchronization process,
/// including fetching the initial snapshot and buffering WebSocket events.
/// </summary>
public class BinanceBookManager
{
    private readonly ILogger _logger;
    private readonly BinanceRestApiClient _apiClient;
    private readonly bool _isDerivatives = false;
    private readonly int _instrumentId;
    private readonly Action<MarketDataEvent> _eventDispatcher;
    private readonly ConcurrentQueue<BufferedDepthUpdate> _eventBuffer = new();
    private readonly object _syncLock = new();
    private long _lastUpdateId = -1;
    private volatile bool _isSnapshotLoaded = false;
    private volatile bool _isFirstEventProcessed = false;
    private static readonly ArrayPool<PriceLevelEntry> _entryPool = ArrayPool<PriceLevelEntry>.Shared;

    public BinanceBookManager(ILogger logger, BinanceRestApiClient apiClient, int instrumentId, Action<MarketDataEvent> eventDispatcher)
    {
        _logger = logger;
        _apiClient = apiClient;
        _instrumentId = instrumentId;
        _eventDispatcher = eventDispatcher;
        _isDerivatives = apiClient.ProdType != ProductType.Spot;
    }

    /// <summary>
    /// Starts the synchronization process by fetching a depth snapshot.
    /// </summary>
    public async Task StartSyncAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller($"Starting order book sync for {_instrumentId}...");
        lock (_syncLock)
        {
            _isSnapshotLoaded = false;
            _isFirstEventProcessed = false;
            _lastUpdateId = -1;
            while (_eventBuffer.TryDequeue(out var update))
            {
                _entryPool.Return(update.Entries);
            }
        }
        await FetchAndApplySnapshotAsync(cancellationToken);
    }

    public void ProcessWsUpdate(ReadOnlyMemory<byte> payload)
    {
        var reader = new Utf8JsonReader(payload.Span);
        var update = ParseRawUpdate(ref reader);

        lock (_syncLock)
        {
            if (!_isSnapshotLoaded)
            {
                _eventBuffer.Enqueue(update);
                return;
            }
        }

        ValidateAndDispatchUpdate(update);
    }

    private BufferedDepthUpdate ParseRawUpdate(ref Utf8JsonReader reader)
    {
        long u = 0, U = 0, pu = 0, E = 0;
        var entries = _entryPool.Rent(256);
        int count = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.ValueSpan;
            reader.Read();

            if (prop.SequenceEqual("u"u8)) u = reader.GetInt64();
            else if (prop.SequenceEqual("U"u8)) U = reader.GetInt64();
            else if (prop.SequenceEqual("pu"u8)) pu = reader.GetInt64();
            else if (prop.SequenceEqual("E"u8)) E = reader.GetInt64();
            else if (prop.SequenceEqual("b"u8)) ParseEntries(ref reader, ref entries, ref count, Side.Buy);
            else if (prop.SequenceEqual("a"u8)) ParseEntries(ref reader, ref entries, ref count, Side.Sell);
        }

        return new BufferedDepthUpdate { u = u, U = U, pu = pu, E = E, Entries = entries, EntryCount = count };
    }

    private void ParseEntries(ref Utf8JsonReader reader, ref PriceLevelEntry[] buffer, ref int currentIdx, Side side)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            // 1. 버퍼 크기 체크 및 동적 확장 (핵심 수정 사항)
            if (currentIdx >= buffer.Length)
            {
                var newBuffer = _entryPool.Rent(buffer.Length * 2);
                Array.Copy(buffer, 0, newBuffer, 0, buffer.Length);
                _entryPool.Return(buffer); // 기존 작은 버퍼 반납
                buffer = newBuffer; // 새 버퍼로 교체
            }

            // 2. 내부 배열 [price, qty] 파싱
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                reader.Read(); // Price 문자열 위치로 이동
                FastJsonParser.TryParseDecimal(ref reader, out decimal p);
                reader.Read(); // Qty 문자열 위치로 이동
                FastJsonParser.TryParseDecimal(ref reader, out decimal q);
                reader.Read(); // Inner Array 종료 (EndArray)

                buffer[currentIdx++] = new PriceLevelEntry(side, p, q);
            }
        }
    }

    private async Task FetchAndApplySnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var limit = _isDerivatives ? 1000 : 5000;
            var snapshot = await _apiClient.GetDepthSnapshotAsync(_instrumentId, limit, cancellationToken);

            lock (_syncLock)
            {
                // 버퍼 내 이벤트 필터링
                while (_eventBuffer.TryPeek(out var update))
                {
                    bool shouldDrop = _isDerivatives ? update.u < snapshot.LastUpdateId : update.u <= snapshot.LastUpdateId;
                    if (shouldDrop)
                    {
                        _eventBuffer.TryDequeue(out var dropped);
                        _entryPool.Return(dropped.Entries);
                    }
                    else break;
                }

                // 스냅샷 전송
                DispatchSnapshot(snapshot);
                _lastUpdateId = snapshot.LastUpdateId;

                // 버퍼에 쌓인 데이터 처리
                while (_eventBuffer.TryDequeue(out var bufferedEvent))
                {
                    ValidateAndDispatchUpdate(bufferedEvent);
                }

                _isSnapshotLoaded = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Failed sync for {_instrumentId}");
        }
    }

    private void ValidateAndDispatchUpdate(BufferedDepthUpdate update)
    {
        try
        {
            if (update.u <= _lastUpdateId) return;

            if (_isDerivatives)
            {
                if (!_isFirstEventProcessed) _isFirstEventProcessed = true;
                else if (update.pu != _lastUpdateId) { TriggerResync(); return; }
            }
            else
            {
                if (update.U > _lastUpdateId + 1) { TriggerResync(); return; }
            }

            // 디스패치 로직 (40개씩 끊어서 전송)
            for (int i = 0; i < update.EntryCount; i += 40)
            {
                var updatesArray = new PriceLevelEntryArray();
                int chunkSize = Math.Min(40, update.EntryCount - i);
                for (int j = 0; j < chunkSize; j++) updatesArray[j] = update.Entries[i + j];

                _eventDispatcher(new MarketDataEvent(update.u, update.E, EventKind.Update, _instrumentId, ExchangeEnum.BINANCE, update.U, BinanceTopic.DepthUpdate.TopicId, chunkSize, updatesArray, (i + 40) >= update.EntryCount));
            }
            _lastUpdateId = update.u;
        }
        finally
        {
            _entryPool.Return(update.Entries); // 처리가 끝나면 반드시 반납
        }
    }

    private void TriggerResync()
    {
        _logger.LogWarningWithCaller($"GAP detected for {_instrumentId}. Resyncing...");
        _ = Task.Run(() => StartSyncAsync(CancellationToken.None));
    }

    // Helper methods to create and dispatch MarketDataEvent
    private void DispatchSnapshot(BinanceDepthSnapshot snapshot)
    {
        _logger.LogInformationWithCaller($"Dispatching snapshot for {_instrumentId} with {snapshot.Bids.Count} bids and {snapshot.Asks.Count} asks.");

        // Combine bids and asks into a single list for easier processing.
        var allLevels = new List<PriceLevelEntry>(snapshot.Bids.Count + snapshot.Asks.Count);
        foreach (var (price, quantity) in snapshot.Bids)
        {
            allLevels.Add(new PriceLevelEntry(Side.Buy, price, quantity));
        }
        foreach (var (price, quantity) in snapshot.Asks)
        {
            allLevels.Add(new PriceLevelEntry(Side.Sell, price, quantity));
        }

        // Process all levels in chunks of 40 (the size of PriceLevelEntryArray).
        for (int chunkStart = 0; chunkStart < allLevels.Count; chunkStart += 40)
        {
            bool isLastChunk = (chunkStart + 40) >= allLevels.Count;
            var updatesArray = new PriceLevelEntryArray();

            // Determine the size of the current chunk.
            int chunkSize = Math.Min(40, allLevels.Count - chunkStart);

            // Populate the inline array for the current chunk.
            for (int i = 0; i < chunkSize; i++)
            {
                updatesArray[i] = allLevels[chunkStart + i];
            }

            var eventKind = (chunkStart == 0) ? EventKind.Snapshot : EventKind.Update;

            var marketEvent = new MarketDataEvent(
                snapshot.LastUpdateId, // All chunks share the same sequence ID
                snapshot.MessageOutputTime,
                eventKind,
                _instrumentId,
                ExchangeEnum.BINANCE,
                0, // Snapshots don't have a PrevSequence
                BinanceTopic.DepthUpdate.TopicId,
                chunkSize,
                updatesArray,
                isLastChunk
            );

            _eventDispatcher(marketEvent);
        }
    }

    private void DispatchUpdate(JsonElement data)
    {
        var u = data.GetProperty("u").GetInt64();
        var U = data.GetProperty("U").GetInt64();
        var eventTime = data.GetProperty("E").GetInt64();

        // 2. 전체 업데이트 개수 파악하여 한 번에 버퍼 대여
        int bidCount = data.TryGetProperty("b", out var bEle) ? bEle.GetArrayLength() : 0;
        int askCount = data.TryGetProperty("a", out var aEle) ? aEle.GetArrayLength() : 0;
        int totalCount = bidCount + askCount;

        if (totalCount == 0)
        {
            _lastUpdateId = u;
            return;
        }

        // 3. List 대신 ArrayPool에서 배열 대여 (Heap 할당 0)
        PriceLevelEntry[] tempBuffer = _entryPool.Rent(totalCount);
        int currentIdx = 0;

        try
        {
            // 4. Bids 처리 (문자열 할당 없이 파싱)
            if (bidCount > 0)
            {
                foreach (var bid in bEle.EnumerateArray())
                {
                    tempBuffer[currentIdx++] = new PriceLevelEntry(
                        Side.Buy,
                        FastParseDecimal(bid[0]),
                        FastParseDecimal(bid[1])
                    );
                }
            }

            // 5. Asks 처리
            if (askCount > 0)
            {
                foreach (var ask in aEle.EnumerateArray())
                {
                    tempBuffer[currentIdx++] = new PriceLevelEntry(
                        Side.Sell,
                        FastParseDecimal(ask[0]),
                        FastParseDecimal(ask[1])
                    );
                }
            }

            // 6. 40개씩 쪼개서 디스럽터(RingBuffer)로 전송
            for (int chunkStart = 0; chunkStart < totalCount; chunkStart += 40)
            {
                var updatesArray = new PriceLevelEntryArray();
                int chunkSize = Math.Min(40, totalCount - chunkStart);

                for (int i = 0; i < chunkSize; i++)
                {
                    updatesArray[i] = tempBuffer[chunkStart + i];
                }

                bool isLastChunk = (chunkStart + 40) >= totalCount;

                var marketEvent = new MarketDataEvent(
                    u, eventTime, EventKind.Update, _instrumentId, ExchangeEnum.BINANCE,
                    U, BinanceTopic.DepthUpdate.TopicId, chunkSize, updatesArray, isLastChunk
                );

                _eventDispatcher(marketEvent);
            }
        }
        finally
        {
            // 7. 사용이 끝난 배열은 반드시 풀에 반납
            _entryPool.Return(tempBuffer);
        }

        _lastUpdateId = u;
    }

    /// <summary>
    /// 문자열 할당 없이 JsonElement에서 decimal을 직접 파싱하는 고성능 메서드
    /// </summary>
    private decimal FastParseDecimal(JsonElement element)
    {
        // 1. 이미 숫자인 경우 바로 반환
        if (element.ValueKind == JsonValueKind.Number) return element.GetDecimal();

        // 2. 문자열인 경우 ("123.45") 원시 텍스트에서 따옴표를 떼고 바이트 단위로 파싱
        // GetRawText()는 .NET 6/7/8에서 매우 효율적으로 동작하며 
        // 최신 버전에서는 문자열 생성을 최소화합니다.
        ReadOnlySpan<char> rawSpan = element.GetRawText().AsSpan();

        // 따옴표 제거 (예: "123.45" -> 123.45)
        if (rawSpan.Length >= 2 && rawSpan[0] == '"')
        {
            rawSpan = rawSpan.Slice(1, rawSpan.Length - 2);
        }

        return decimal.Parse(rawSpan);
    }
}