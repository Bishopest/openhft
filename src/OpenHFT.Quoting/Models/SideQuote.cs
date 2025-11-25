using System;
using System.Text.Json.Serialization;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting.Models;

public readonly struct SideQuote
{
    public readonly Side Side { get; }
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
    public SideQuote(Side side, Price price, Quantity size)
    {
        Side = side;
        Price = price;
        Size = size;
    }

    // It's good practice to include equality members for structs.
    public override bool Equals(object? obj) => obj is SideQuote other && Equals(other);

    public bool Equals(SideQuote other) => Side.Equals(other.Side) && Price.Equals(other.Price) && Size.Equals(other.Size);

    public override int GetHashCode() => HashCode.Combine(Side, Price, Size);

    public static bool operator ==(SideQuote left, SideQuote right) => left.Equals(right);

    public static bool operator !=(SideQuote left, SideQuote right) => !left.Equals(right);

    public override string ToString() => $"Side: {Side}, Price: {Price}, Size: {Size}";


}
