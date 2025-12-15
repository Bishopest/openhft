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
    private readonly int _instrumentId;
    private readonly Action<MarketDataEvent> _eventDispatcher;
    private readonly ConcurrentQueue<JsonElement> _eventBuffer = new();
    private readonly object _syncLock = new();
    private long _lastUpdateId = -1;
    private volatile bool _isSnapshotLoaded = false;

    public BinanceBookManager(ILogger logger, BinanceRestApiClient apiClient, int instrumentId, Action<MarketDataEvent> eventDispatcher)
    {
        _logger = logger;
        _apiClient = apiClient;
        _instrumentId = instrumentId;
        _eventDispatcher = eventDispatcher;
    }

    /// <summary>
    /// Starts the synchronization process by fetching a depth snapshot.
    /// </summary>
    public async Task StartSyncAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller($"Starting order book sync for {_instrumentId}...");
        _isSnapshotLoaded = false;
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

            // --- MODIFICATION: Entire validation and initial processing is now under a lock ---
            lock (_syncLock)
            {
                // Discard any buffered events that are older than our new snapshot.
                while (_eventBuffer.TryPeek(out var bufferedEvent) && bufferedEvent.GetProperty("u").GetInt64() <= snapshotUpdateId)
                {
                    _eventBuffer.TryDequeue(out _);
                }

                // Now, check the first event in the (cleaned) buffer.
                if (_eventBuffer.TryPeek(out var firstEvent))
                {
                    var U = firstEvent.GetProperty("U").GetInt64();
                    if (U > snapshotUpdateId + 1)
                    {
                        // Gap detected. Snapshot is stale. We need to refetch.
                        // We do this by simply returning. The background task will try again.
                        _logger.LogWarningWithCaller($"Gap detected between snapshot ({snapshotUpdateId}) and first buffered event ({U}). Aborting this sync attempt to refetch.");
                        // Start a new fetch task in the background without awaiting
                        _ = Task.Run(() => FetchAndApplySnapshotAsync(cancellationToken), cancellationToken);
                        return; // Exit this attempt
                    }
                }

                // If we are here, the snapshot is valid against the current buffer.

                // 1. Dispatch the snapshot.
                DispatchSnapshot(snapshot);
                _lastUpdateId = snapshotUpdateId;

                // 2. Process all valid buffered events immediately.
                _logger.LogInformationWithCaller($"Snapshot for {_instrumentId} applied. Processing {_eventBuffer.Count} buffered events.");
                while (_eventBuffer.TryDequeue(out var bufferedEvent))
                {
                    // Here we directly dispatch, assuming they are now valid.
                    // A stricter check could be added inside the loop too.
                    DispatchUpdate(bufferedEvent);
                }

                // 3. ONLY NOW, after snapshot and buffer are cleared, we declare sync complete.
                _isSnapshotLoaded = true;
                _logger.LogInformationWithCaller($"Sync for {_instrumentId} complete. Live processing will now resume.");
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

        if (u <= _lastUpdateId)
        {
            return; // Ignore old event
        }

        if (U > _lastUpdateId + 1)
        {
            _logger.LogWarningWithCaller($"GAP DETECTED during live processing for {_instrumentId}. Triggering resync.");
            _ = StartSyncAsync(CancellationToken.None);
            return;
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
                updatesArray
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
                updatesArray
            );

            _eventDispatcher(marketEvent);
        }

        // 4. IMPORTANT: Update the internal sequence number AFTER all chunks are dispatched.
        _lastUpdateId = u;

        // --- MODIFICATION END ---
    }
}