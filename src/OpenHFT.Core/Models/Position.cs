using System;

namespace OpenHFT.Core.Models;

/// <summary>
/// Represents the position of a single instrument at a point in time.
/// This is an immutable value type. State transitions are handled by creating new instances.
/// </summary>
public readonly struct Position : IEquatable<Position>
{
    /// <summary>
    /// The unique identifier for the instrument.
    /// </summary>
    public readonly int InstrumentId { get; }

    /// <summary>
    /// The current quantity of the position.
    /// Positive for a long position, negative for a short position, zero for flat.
    /// </summary>
    public readonly Quantity Quantity { get; }

    /// <summary>
    /// The volume-weighted average price of the current position.
    /// </summary>
    public readonly Price AverageEntryPrice { get; }

    /// <summary>
    /// The timestamp of the last fill that updated this position.
    /// </summary>
    public readonly long LastUpdateTime { get; }

    // Add other relevant metrics like RealizedPnL if needed.

    // A default, "flat" position.
    public static Position Zero(int instrumentId) => new(instrumentId, Quantity.FromDecimal(0m), Price.FromDecimal(0m), 0);

    public Position(int instrumentId, Quantity quantity, Price averageEntryPrice, long lastUpdateTime)
    {
        InstrumentId = instrumentId;
        Quantity = quantity;
        AverageEntryPrice = averageEntryPrice;
        LastUpdateTime = lastUpdateTime;
    }

    /// <summary>
    /// Applies a new fill to the current position and returns a new, updated Position struct.
    /// This is a pure function and does not modify the current instance.
    /// </summary>
    /// <param name="fill">The fill to apply.</param>
    /// <returns>A new Position struct reflecting the state after the fill.</returns>
    public Position ApplyFill(Fill fill)
    {
        if (fill.InstrumentId != this.InstrumentId)
        {
            // This should not happen.
            throw new ArgumentException("Fill is for a different instrument.", nameof(fill));
        }

        var fillQtyDecimal = fill.Quantity.ToDecimal();
        var fillPriceDecimal = fill.Price.ToDecimal();
        var currentQtyDecimal = this.Quantity.ToDecimal();
        var currentAvgPriceDecimal = this.AverageEntryPrice.ToDecimal();

        decimal newQtyDecimal;
        decimal newAvgPriceDecimal;

        // Determine the direction of the fill relative to the position
        var effectiveFillQty = fill.Side == Side.Buy ? fillQtyDecimal : -fillQtyDecimal;

        newQtyDecimal = currentQtyDecimal + effectiveFillQty;

        const decimal Epsilon = 0.00000001m;
        bool isCurrentQtyFlat = Math.Abs(currentQtyDecimal) < Epsilon;
        bool isNewQtyFlat = Math.Abs(newQtyDecimal) < Epsilon;


        if (isNewQtyFlat)
        {
            // 1. 포지션이 청산되면 평균 단가는 0
            newAvgPriceDecimal = 0m;
        }
        else if (isCurrentQtyFlat || Math.Sign(currentQtyDecimal) != Math.Sign(newQtyDecimal))
        {
            // 2. 포지션이 새로 시작되거나 (isCurrentQtyFlat), 
            //    포지션 방향이 반전되면 (Reversal), 평균 단가를 채결 가격으로 초기화
            //    (참고: 기존 로직에서는 반전 시에도 '증가/감소' 체크가 있었는데, 일반적으로 반전 시 초기화하는 경우가 많아 이를 따름)
            newAvgPriceDecimal = fillPriceDecimal;
        }
        else // 포지션 방향이 유지되며, 포지션이 Flat이 아님
        {
            if (Math.Abs(currentQtyDecimal) < Math.Abs(newQtyDecimal))
            {
                // 3. 포지션이 증가하면 가중 평균 계산 (같은 방향 추가)
                newAvgPriceDecimal = ((currentQtyDecimal * currentAvgPriceDecimal) + (effectiveFillQty * fillPriceDecimal)) / newQtyDecimal;
            }
            else
            {
                // 4. 포지션이 감소하면 (Close-out 또는 Partial Close) 기존 평균 유지
                newAvgPriceDecimal = currentAvgPriceDecimal;
            }
        }

        return new Position(
            this.InstrumentId,
            Quantity.FromDecimal(newQtyDecimal),
            Price.FromDecimal(newAvgPriceDecimal),
            fill.Timestamp
        );
    }

    // --- IEquatable implementation ---
    public bool Equals(Position other) =>
        InstrumentId == other.InstrumentId &&
        Quantity.Equals(other.Quantity) &&
        AverageEntryPrice.Equals(other.AverageEntryPrice);

    public override bool Equals(object? obj) => obj is Position other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(InstrumentId, Quantity, AverageEntryPrice);
    public static bool operator ==(Position left, Position right) => left.Equals(right);
    public static bool operator !=(Position left, Position right) => !left.Equals(right);
    public override string ToString() =>
        $"Pos: {Quantity.ToDecimal()} | AvgPx: {AverageEntryPrice.ToDecimal()} (ID: {InstrumentId})";
}
