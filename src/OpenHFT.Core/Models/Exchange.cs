using System;

namespace OpenHFT.Core.Models;

/// <summary>
/// Enum representing the supported cryptocurrency exchanges.
/// Used for type-safe identification of exchanges.
/// </summary>
public enum ExchangeEnum
{
    Undefined = 0, // 기본값 또는 알 수 없는 경우
    BINANCE = 101,
    BITMEX = 102,
    BYBIT = 103,
    BITGET = 104,
    BITHUMB = 105,
    COINONE = 106,
}

/// <summary>
/// Cryptocurrency Exchanges Collection: Provides constant string identifiers for exchanges
/// and mapping utilities for their internal integer codes.
/// </summary>
public static class Exchange
{
    private const int MaxExchangeIdentifier = 2000;
    // Internal maps for string <-> int conversion
    private static Dictionary<string, ExchangeEnum> Exchanges = new Dictionary<string, ExchangeEnum>();
    private static Dictionary<ExchangeEnum, string> ReverseExchanges = new Dictionary<ExchangeEnum, string>();

    private static readonly IEnumerable<Tuple<string, ExchangeEnum>> HardcodedExchanges = new List<Tuple<string, ExchangeEnum>>
        {
            Tuple.Create("empty_exchange", ExchangeEnum.Undefined),
            Tuple.Create(BINANCE, ExchangeEnum.BINANCE),
            Tuple.Create(BITMEX, ExchangeEnum.BITMEX),
            Tuple.Create(BYBIT, ExchangeEnum.BYBIT),
            Tuple.Create(BITGET, ExchangeEnum.BITGET),
            Tuple.Create(BITHUMB, ExchangeEnum.BITHUMB),
            Tuple.Create(COINONE, ExchangeEnum.COINONE),
            // Add other exchanges here as needed
        };

    // Static constructor to initialize our maps from HardcodedExchanges
    static Exchange()
    {
        foreach (var exchange in HardcodedExchanges)
        {
            if (Exchanges.ContainsKey(exchange.Item1))
            {
                throw new InvalidOperationException($"Duplicate exchange name '{exchange.Item1}' found in hardcoded list.");
            }
            if (ReverseExchanges.ContainsKey(exchange.Item2))
            {
                throw new InvalidOperationException($"Duplicate exchange enum '{exchange.Item2}' found in hardcoded list.");
            }

            Exchanges[exchange.Item1] = exchange.Item2;
            ReverseExchanges[exchange.Item2] = exchange.Item1;
        }
    }

    public const string BINANCE = "binance";

    public const string BITMEX = "bitmex";

    public const string BYBIT = "bybit";

    public const string BITGET = "bitget";

    public const string BITHUMB = "bithumb";

    public const string COINONE = "coinone";

    /// <summary>
    /// Gets the internal ExchangeEnum code for the specified exchange name.
    /// Returns <c>null</c> if the exchange is not found.
    /// </summary>
    /// <param name="exchangeName">The exchange name to check for (case-insensitive)</param>
    /// <returns>The internal ExchangeEnum used for the exchange, or null if not found.</returns>
    public static ExchangeEnum? Encode(string exchangeName)
    {
        return Exchanges.TryGetValue(exchangeName.ToLowerInvariant(), out var code) ? (ExchangeEnum?)code : null;
    }

    /// <summary>
    /// Gets the string representation of the exchange for the specified internal ExchangeEnum code.
    /// Returns <c>null</c> if the code is not found.
    /// </summary>
    /// <param name="code">The ExchangeEnum code to be decoded</param>
    /// <returns>The string representation of the exchange, or null if not found.</returns>
    public static string? Decode(ExchangeEnum code)
    {
        return ReverseExchanges.TryGetValue(code, out var exchangeName) ? exchangeName : null;
    }

    /// <summary>
    /// Returns a list of the supported exchange names.
    /// </summary>
    public static List<string> SupportedExchanges()
    {
        return Exchanges.Keys.ToList();
    }

    /// <summary>
    /// Returns a list of the supported ExchangeEnum identifiers.
    /// </summary>
    public static List<ExchangeEnum> SupportedExchangeEnums()
    {
        return Exchanges.Values.ToList();
    }
}
