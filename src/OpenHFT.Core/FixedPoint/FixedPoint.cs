using System;
using System.Runtime.CompilerServices;

namespace OpenHFT.Core.FixedPoint;

/// <summary>
/// Represents a generic fixed-point number using a long for internal ticks.
/// TScale determines the precision.
/// This struct is immutable.
/// </summary>
/// <typeparam name="TScale">A struct that implements IScale to define the scaling factor.</typeparam>
public readonly struct FixedPoint<TScale> : IEquatable<FixedPoint<TScale>>, IComparable<FixedPoint<TScale>>
    where TScale : struct, IScale
{
    private static readonly TScale ScaleInfo = new();
    private static readonly decimal Scale = ScaleInfo.Value;

    // The internal representation of the fixed-point number.
    private readonly long _ticks;

    // Private constructor to be used internally.
    private FixedPoint(long ticks)
    {
        _ticks = ticks;
    }

    // --- Factory Methods ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixedPoint<TScale> FromDecimal(decimal value)
    {
        return new FixedPoint<TScale>((long)(value * Scale));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixedPoint<TScale> FromTicks(long ticks)
    {
        return new FixedPoint<TScale>(ticks);
    }

    // --- Conversion Methods ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public decimal ToDecimal()
    {
        return _ticks / Scale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ToTicks()
    {
        return _ticks;
    }

    // --- Operator Overloading ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixedPoint<TScale> operator +(FixedPoint<TScale> a, FixedPoint<TScale> b)
        => new(ticks: a._ticks + b._ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixedPoint<TScale> operator -(FixedPoint<TScale> a, FixedPoint<TScale> b)
        => new(ticks: a._ticks - b._ticks);

    // Note: Multiplication/Division between two fixed-point numbers can be complex.
    // For simplicity, we only implement operations with integers and decimals here.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixedPoint<TScale> operator *(FixedPoint<TScale> a, long multiplier)
        => new(ticks: a._ticks * multiplier);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixedPoint<TScale> operator *(FixedPoint<TScale> a, decimal multiplier)
        => new(ticks: (long)(a._ticks * multiplier));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixedPoint<TScale> operator /(FixedPoint<TScale> a, long divisor)
        => new(ticks: a._ticks / divisor);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixedPoint<TScale> operator /(FixedPoint<TScale> a, decimal divisor)
        => new(ticks: (long)(a._ticks / divisor));

    // --- Comparison Operators ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(FixedPoint<TScale> a, FixedPoint<TScale> b) => a._ticks == b._ticks;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(FixedPoint<TScale> a, FixedPoint<TScale> b) => a._ticks != b._ticks;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(FixedPoint<TScale> a, FixedPoint<TScale> b) => a._ticks > b._ticks;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(FixedPoint<TScale> a, FixedPoint<TScale> b) => a._ticks < b._ticks;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(FixedPoint<TScale> a, FixedPoint<TScale> b) => a._ticks >= b._ticks;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(FixedPoint<TScale> a, FixedPoint<TScale> b) => a._ticks <= b._ticks;

    // --- Interface Implementations & Overrides ---

    public override bool Equals(object? obj) => obj is FixedPoint<TScale> other && Equals(other);
    public bool Equals(FixedPoint<TScale> other) => _ticks == other._ticks;
    public int CompareTo(FixedPoint<TScale> other) => _ticks.CompareTo(other._ticks);
    public override int GetHashCode() => _ticks.GetHashCode();
    public override string ToString() => ToDecimal().ToString();
}