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
    private readonly ConcurrentQueue<JsonElement> _eventBuffer = new();
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
        _isSnapshotLoaded = false;
        _isFirstEventProcessed = false;
        _lastUpdateId = -1;
        while (!_eventBuffer.IsEmpty) _eventBuffer.TryDequeue(out _);

        await FetchAndApplySnapshotAsync(cancellationToken);
    }

    public void ProcessWsUpdate(JsonElement data)
    {
        // Use a lock to prevent race condition between this and FetchAndApplySnapshotAsync
        lock (_syncLock)
        {
            if (!_isSnapshotLoaded)
            {
                _eventBuffer.Enqueue(data.Clone());
                return;
            }
        }

        // If snapshot is loaded, validation and dispatch can happen outside the lock.
        ValidateAndDispatchUpdate(data);
    }

    private async Task FetchAndApplySnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var limit = _isDerivatives ? 1000 : 5000;
            var snapshot = await _apiClient.GetDepthSnapshotAsync(_instrumentId, limit, cancellationToken);
            var snapshotUpdateId = snapshot.LastUpdateId;
            _logger.LogDebug($"Fetched snapshot for {_instrumentId} with lastUpdateId: {snapshotUpdateId}");

            lock (_syncLock)
            {
                if (_isDerivatives)
                {
                    // [Derivatives] u < lastUpdateId 인 이벤트는 모두 버림
                    while (_eventBuffer.TryPeek(out var bEvent) && bEvent.GetProperty("u").GetInt64() < snapshotUpdateId)
                    {
                        _eventBuffer.TryDequeue(out _);
                    }

                    // [Derivatives] 첫 이벤트 조건: U <= lastUpdateId AND u >= lastUpdateId
                    if (_eventBuffer.TryPeek(out var firstEvent))
                    {
                        var U = firstEvent.GetProperty("U").GetInt64();
                        var u = firstEvent.GetProperty("u").GetInt64();

                        if (!(U <= snapshotUpdateId && u >= snapshotUpdateId))
                        {
                            _logger.LogWarningWithCaller($"[Derivatives] Gap detected. Snapshot ID {snapshotUpdateId} not covered by first event [U:{U}, u:{u}]. Refetching...");
                            _ = Task.Run(() => StartSyncAsync(cancellationToken));
                            return;
                        }
                    }
                }
                else
                {
                    // [Spot] u <= lastUpdateId 인 이벤트는 모두 버림
                    while (_eventBuffer.TryPeek(out var bEvent) && bEvent.GetProperty("u").GetInt64() <= snapshotUpdateId)
                    {
                        _eventBuffer.TryDequeue(out _);
                    }

                    if (_eventBuffer.TryPeek(out var firstEvent))
                    {
                        var U = firstEvent.GetProperty("U").GetInt64();
                        if (U > snapshotUpdateId + 1)
                        {
                            _logger.LogWarningWithCaller($"[Spot] Gap detected. Snapshot: {snapshotUpdateId}, First Event U: {U}. Refetching...");
                            _ = Task.Run(() => StartSyncAsync(cancellationToken));
                            return;
                        }
                    }
                }

                // 스냅샷 적용
                DispatchSnapshot(snapshot);
                _lastUpdateId = snapshotUpdateId;

                while (_eventBuffer.TryDequeue(out var bufferedEvent))
                {
                    ValidateAndDispatchUpdate(bufferedEvent);
                }

                _isSnapshotLoaded = true;
                _logger.LogInformationWithCaller($"Sync for {_instrumentId} complete.");
            }
            // --- End of lock ---
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Failed to fetch or apply snapshot for {_instrumentId}. Will retry on next subscription.");
        }
    }

    private void ValidateAndDispatchUpdate(JsonElement data)
    {
        long u = data.GetProperty("u").GetInt64();
        long U = data.GetProperty("U").GetInt64();

        // 1. 이미 처리된 이전 데이터는 무시
        if (u <= _lastUpdateId) return;

        // 2. Gap Detection
        if (_isDerivatives)
        {
            // [Derivatives] pu(Previous Update ID) 필드 확인
            if (data.TryGetProperty("pu", out var puElement))
            {
                long pu = puElement.GetInt64();

                if (!_isFirstEventProcessed)
                {
                    _isFirstEventProcessed = true;
                }
                else
                {
                    if (pu != _lastUpdateId)
                    {
                        _logger.LogWarningWithCaller($"[Derivatives] GAP: pu({pu}) != lastUpdateId({_lastUpdateId}) for {_instrumentId}. Resyncing...");
                        _ = StartSyncAsync(CancellationToken.None);
                        return;
                    }
                }
            }
        }
        else
        {
            // [Spot] U == lastUpdateId + 1 확인
            if (U > _lastUpdateId + 1)
            {
                _logger.LogWarningWithCaller($"[Spot] GAP: U({U}) > lastUpdateId({_lastUpdateId}) + 1 for {_instrumentId}. Resyncing...");
                _ = StartSyncAsync(CancellationToken.None);
                return;
            }
        }

        DispatchUpdate(data);
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