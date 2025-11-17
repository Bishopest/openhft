using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

/// <summary>
/// A central service for managing and querying positions for all instruments.
/// </summary>
public interface IPositionManager
{
    /// <summary>
    /// Fired whenever any instrument's position changes.
    /// </summary>
    event EventHandler<Position> PositionChanged;

    /// <summary>
    /// Gets the current position for a specific instrument.
    /// </summary>
    /// <param name="instrumentId">The ID of the instrument.</param>
    /// <returns>The current position. Returns a zero position if no position exists.</returns>
    Position GetPosition(int instrumentId);

    /// <summary>
    /// Gets a snapshot of all current positions.
    /// </summary>
    IReadOnlyDictionary<int, Position> GetAllPositions();
}