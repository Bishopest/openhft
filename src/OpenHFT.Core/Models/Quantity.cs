using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenHFT.Core.FixedPoint;

namespace OpenHFT.Core.Models;

public class QuantityJsonConverter : JsonConverter<Quantity>
{
    public override Quantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return Quantity.FromDecimal(reader.GetDecimal());
    }

    public override void Write(Utf8JsonWriter writer, Quantity value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.ToDecimal());
    }
}

[JsonConverter(typeof(QuantityJsonConverter))]
public readonly struct Quantity : IEquatable<Quantity>, IComparable<Quantity>
{
    private readonly FixedPoint<QuantityScale> _value;

    private Quantity(FixedPoint<QuantityScale> value) => _value = value;

    // Static methods
    public static Quantity Zero => new(FixedPoint<QuantityScale>.FromDecimal(0m));

    // Factory methods
    public static Quantity FromDecimal(decimal value) => new(FixedPoint<QuantityScale>.FromDecimal(value));
    public static Quantity FromTicks(long ticks) => new(FixedPoint<QuantityScale>.FromTicks(ticks));

    // Conversions
    public decimal ToDecimal() => _value.ToDecimal();
    public long ToTicks() => _value.ToTicks();

    // Operator overloading
    public static Quantity operator +(Quantity a, Quantity b) => new(a._value + b._value);
    public static Quantity operator -(Quantity a, Quantity b) => new(a._value - b._value);

    // More operators can be added as needed (e.g., Quantity * decimal)

    public static bool operator >(Quantity a, Quantity b) => a._value > b._value;
    public static bool operator <(Quantity a, Quantity b) => a._value < b._value;
    public static bool operator >=(Quantity a, Quantity b) => a._value >= b._value;
    public static bool operator <=(Quantity a, Quantity b) => a._value <= b._value;
    public static bool operator ==(Quantity a, Quantity b) => a._value == b._value;
    public static bool operator !=(Quantity a, Quantity b) => a._value != b._value;

    // --- Interface Implementations & Overrides ---
    public override bool Equals(object? obj) => obj is Quantity other && Equals(other);
    public bool Equals(Quantity other) => _value.Equals(other._value);
    public int CompareTo(Quantity other) => _value.CompareTo(other._value);
    public override int GetHashCode() => _value.GetHashCode();
    public override string ToString() => _value.ToString();
}
