using System;

namespace OpenHFT.Core.Instruments;

/// <summary>
/// Represents a currency, such as BTC or USDT.
/// Designed as an immutable value type for efficiency and safety.
/// </summary>
public readonly struct Currency : IEquatable<Currency>
{
    public string Symbol { get; }

    private Currency(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Currency symbol cannot be null or whitespace.", nameof(symbol));
        }
        Symbol = symbol.ToUpperInvariant();
    }

    // Static factory method to create or get currency instances
    public static Currency FromString(string symbol) => new(symbol);

    // Common currencies for convenience
    public static readonly Currency BTC = new("BTC");
    public static readonly Currency ETH = new("ETH");
    public static readonly Currency USDT = new("USDT");
    public static readonly Currency USD = new("USD");
    public static readonly Currency KRW = new("KRW");

    // Equality implementation
    public bool Equals(Currency other) => Symbol == other.Symbol;
    public override bool Equals(object? obj) => obj is Currency other && Equals(other);
    public override int GetHashCode() => Symbol.GetHashCode();
    public override string ToString() => Symbol;

    public static bool operator ==(Currency left, Currency right) => left.Equals(right);
    public static bool operator !=(Currency left, Currency right) => !left.Equals(right);
}