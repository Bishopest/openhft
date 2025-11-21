using System;
using OpenHFT.Core.Instruments;

namespace OpenHFT.Core.Models;

/// <summary>
/// Represents an amount of money in a specific currency.
/// Immutable value type for financial calculations.
/// </summary>
public readonly struct CurrencyAmount : IEquatable<CurrencyAmount>
{
    /// <summary>
    /// The specific currency (e.g., BTC, USDT).
    /// </summary>
    public readonly Currency Currency { get; }

    /// <summary>
    /// The quantity or value of the currency.
    /// Using decimal ensures high precision for financial amounts.
    /// </summary>
    public readonly decimal Amount { get; }

    // Private constructor ensures creation only through factory methods
    private CurrencyAmount(decimal amount, Currency currency)
    {
        Currency = currency;
        Amount = amount;
    }

    // --- Factory Methods ---

    /// <summary>
    /// Creates a new CurrencyAmount instance.
    /// </summary>
    public static CurrencyAmount FromDecimal(decimal amount, Currency currency) =>
        new(amount, currency);

    // --- Operator Overloading (Optional, but useful for addition/subtraction) ---

    /// <summary>
    /// Adds two currency amounts if and only if they share the same currency.
    /// </summary>
    public static CurrencyAmount operator +(CurrencyAmount a, CurrencyAmount b)
    {
        if (a.Currency != b.Currency)
        {
            throw new InvalidOperationException($"Cannot add different currencies: {a.Currency} and {b.Currency}");
        }
        return new CurrencyAmount(a.Amount + b.Amount, a.Currency);
    }

    public static CurrencyAmount operator -(CurrencyAmount a, CurrencyAmount b)
    {
        if (a.Currency != b.Currency)
        {
            throw new InvalidOperationException($"Cannot subtract different currencies: {a.Currency} and {b.Currency}");
        }

        return new CurrencyAmount(a.Amount - b.Amount, a.Currency);
    }

    public static CurrencyAmount operator *(CurrencyAmount a, decimal b) =>
        new CurrencyAmount(a.Amount * b, a.Currency);

    // --- Equality and Display ---

    public bool Equals(CurrencyAmount other)
    {
        // 두 값과 통화가 모두 같아야 동일한 Money 객체입니다.
        return Amount == other.Amount && Currency.Equals(other.Currency);
    }

    public override bool Equals(object? obj) => obj is CurrencyAmount other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Amount, Currency);

    public override string ToString() => $"{Amount:N8} {Currency.Symbol}";
}