using System;
using System.Text.Json.Serialization;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting.Models;

/// <summary>
/// Represents a single side of a quote, containing a price and a size.
/// This is an immutable value type for performance and thread-safety.
/// </summary>
public readonly struct Quote : IEquatable<Quote>
{
    /// <summary>
    /// The price of the quote.
    /// </summary>
    public readonly Price Price { get; }

    /// <summary>
    /// The size or quantity available at this price.
    /// </summary>
    public readonly Quantity Size { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Quote"/> struct.
    /// </summary>
    /// <param name="price">The price of the quote.</param>
    /// <param name="size">The size of the quote.</param>
    [JsonConstructor]
    public Quote(Price price, Quantity size)
    {
        Price = price;
        Size = size;
    }

    // It's good practice to include equality members for structs.
    public override bool Equals(object? obj) => obj is Quote other && Equals(other);

    public bool Equals(Quote other) => Price.Equals(other.Price) && Size.Equals(other.Size);

    public override int GetHashCode() => HashCode.Combine(Price, Size);

    public static bool operator ==(Quote left, Quote right) => left.Equals(right);

    public static bool operator !=(Quote left, Quote right) => !left.Equals(right);

    public override string ToString() => $"Price: {Price}, Size: {Size}";
}