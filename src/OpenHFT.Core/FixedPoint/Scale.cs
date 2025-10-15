using System;

namespace OpenHFT.Core.FixedPoint;

/// <summary>
/// Defines the scaling contract for fixed-point numbers.
/// </summary>
public interface IScale
{
    decimal Value { get; }
}

/// <summary>
/// Represents the scale for price values (e.g., 100,000,000 for 8 decimal places).
/// </summary>
public readonly struct PriceScale : IScale
{
    public decimal Value => 100_000_000m; // 10^8
}

/// <summary>
/// Represents the scale for quantity values (e.g., 100,000,000 for 8 decimal places).
/// </summary>
public readonly struct QuantityScale : IScale
{
    public decimal Value => 100_000_000m; // 10^8
}