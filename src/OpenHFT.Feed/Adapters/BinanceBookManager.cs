using System;
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
            var snapshot = await _apiClient.GetDepthSnapshotAsync(_instrumentId, 1000, cancellationToken);
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
        // --- MODIFICATION START ---

        // 1. Extract sequence and timestamp information first.
        var u = data.GetProperty("u").GetInt64();
        var U = data.GetProperty("U").GetInt64();
        var eventTime = data.GetProperty("E").GetInt64();

        // 2. Combine all updates (bids and asks) into a single list.
        var allUpdates = new List<PriceLevelEntry>();
        if (data.TryGetProperty("b", out var bidsElement))
        {
            foreach (var bid in bidsElement.EnumerateArray())
            {
                // The quantity "0" indicates a level removal.
                allUpdates.Add(new PriceLevelEntry(Side.Buy, decimal.Parse(bid[0].GetString()!), decimal.Parse(bid[1].GetString()!)));
            }
        }
        if (data.TryGetProperty("a", out var asksElement))
        {
            foreach (var ask in asksElement.EnumerateArray())
            {
                allUpdates.Add(new PriceLevelEntry(Side.Sell, decimal.Parse(ask[0].GetString()!), decimal.Parse(ask[1].GetString()!)));
            }
        }

        if (!allUpdates.Any())
        {
            // If there are no updates in this message, we still need to update the sequence number.
            _lastUpdateId = u;
            return;
        }

        // 3. Process all updates in chunks of 40.
        for (int chunkStart = 0; chunkStart < allUpdates.Count; chunkStart += 40)
        {
            var updatesArray = new PriceLevelEntryArray();
            int chunkSize = Math.Min(40, allUpdates.Count - chunkStart);

            for (int i = 0; i < chunkSize; i++)
            {
                updatesArray[i] = allUpdates[chunkStart + i];
            }

            bool isLastChunk = (chunkStart + 40) >= allUpdates.Count;

            // Create a MarketDataEvent for the current chunk.
            // All chunks from a single WebSocket message share the same sequence info (U and u).
            var marketEvent = new MarketDataEvent(
                u,           // Sequence is 'u' (the final update ID of the whole message)
                eventTime,
                EventKind.Update,
                _instrumentId,
                ExchangeEnum.BINANCE,
                U,           // PrevSequence is 'U' (the first update ID of the whole message)
                BinanceTopic.DepthUpdate.TopicId,
                chunkSize,
                updatesArray,
                isLastChunk
            );

            _eventDispatcher(marketEvent);
        }

        // 4. IMPORTANT: Update the internal sequence number AFTER all chunks are dispatched.
        _lastUpdateId = u;

        // --- MODIFICATION END ---
    }
}