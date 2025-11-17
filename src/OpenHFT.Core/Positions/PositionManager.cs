using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Core.Positions;

public class PositionManager : IPositionManager, IDisposable
{
    private readonly ILogger<PositionManager> _logger;
    private readonly IOrderRouter _orderRouter;
    private readonly IFillRepository _fillRepository;
    private readonly ConcurrentDictionary<int, Position> _positions = new();
    private readonly object _lock = new();
    public event EventHandler<Position>? PositionChanged;

    public PositionManager(
        ILogger<PositionManager> logger,
        IOrderRouter orderRouter,
        IFillRepository fillRepository)
    {
        _logger = logger;
        _orderRouter = orderRouter;
        _fillRepository = fillRepository;
        _orderRouter.OrderFilled += OnOrderFilled;
    }

    private void OnOrderFilled(object? sender, Fill fill)
    {
        _logger.LogInformationWithCaller($"Updating position with new fill: {fill}");
        lock (_lock)
        {
            var currentPosition = GetPosition(fill.InstrumentId);
            var newPosition = currentPosition.ApplyFill(fill);
            _positions[fill.InstrumentId] = newPosition;

            // Fire event to notify subscribers (e.g., QuotingEngine, RiskManager)
            PositionChanged?.Invoke(this, newPosition);
        }
    }

    public async Task RestorePositionsAsync()
    {
        _logger.LogInformationWithCaller("Restoring positions from today's fills...");
        var todayFills = await _fillRepository.GetFillsByDateAsync(DateTime.UtcNow.Date);

        var fillsByInstrument = todayFills.GroupBy(f => f.InstrumentId);

        lock (_lock)
        {
            foreach (var group in fillsByInstrument)
            {
                var instrumentId = group.Key;
                var currentPosition = Position.Zero(instrumentId);
                foreach (var fill in group.OrderBy(f => f.Timestamp))
                {
                    currentPosition = currentPosition.ApplyFill(fill);
                }
                _positions[instrumentId] = currentPosition;
                _logger.LogInformationWithCaller($"Restored position for InstrumentId {instrumentId}: {currentPosition}");
            }
        }
    }

    public IReadOnlyDictionary<int, Position> GetAllPositions()
    {
        return _positions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public Position GetPosition(int instrumentId)
    {
        return _positions.GetOrAdd(instrumentId, Position.Zero(instrumentId));
    }

    public void Dispose()
    {
        _orderRouter.OrderFilled -= OnOrderFilled;
    }
}
