using System;
using OpenHFT.Core.FixedPoint;

namespace OpenHFT.Core.Models;

// Price is a fixed-point number with PriceScale precision.
public readonly struct Price : IEquatable<Price>, IComparable<Price>
{
    private readonly FixedPoint<PriceScale> _value;

    private Price(FixedPoint<PriceScale> value) => _value = value;

    // Factory methods
    public static Price FromDecimal(decimal value) => new(FixedPoint<PriceScale>.FromDecimal(value));
    public static Price FromTicks(long ticks) => new(FixedPoint<PriceScale>.FromTicks(ticks));

    // Conversions
    public decimal ToDecimal() => _value.ToDecimal();
    public long ToTicks() => _value.ToTicks();

    // Operator overloading
    public static Price operator +(Price a, Price b) => new(a._value + b._value);
    public static Price operator -(Price a, Price b) => new(a._value - b._value);

    // More operators can be added as needed (e.g., Price * decimal)

    public static bool operator >(Price a, Price b) => a._value > b._value;
    public static bool operator <(Price a, Price b) => a._value < b._value;
    public static bool operator >=(Price a, Price b) => a._value >= b._value;
    public static bool operator <=(Price a, Price b) => a._value <= b._value;
    public static bool operator ==(Price a, Price b) => a._value == b._value;
    public static bool operator !=(Price a, Price b) => a._value != b._value;

    // --- Interface Implementations & Overrides ---
    public override bool Equals(object? obj) => obj is Price other && Equals(other);
    public bool Equals(Price other) => _value.Equals(other._value);
    public int CompareTo(Price other) => _value.CompareTo(other._value);
    public override int GetHashCode() => _value.GetHashCode();
    public override string ToString() => _value.ToString();
}
