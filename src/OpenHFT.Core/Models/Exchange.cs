using System;


namespace OpenHFT.Core.Models;

/// <summary>
/// Cryptocurrency Exchanges Collection: Provides constant string identifiers for exchanges
/// and mapping utilities for their internal integer codes.
/// </summary>
public static class Exchange
{
    // Internal maps for string <-> int conversion
    private static Dictionary<string, int> Exchanges = new Dictionary<string, int>();
    private static Dictionary<int, string> ReverseExchanges = new Dictionary<int, string>();

    // Hardcoded list of exchanges and their integer identifiers
    private static readonly IEnumerable<Tuple<string, int>> HardcodedExchanges = new List<Tuple<string, int>>
        {
            // Start identifiers from a higher range to distinguish from 'Market' class if needed
            Tuple.Create("empty_exchange", 0), // Default empty exchange
            Tuple.Create(BINANCE, 101),
            Tuple.Create(BITMEX, 102),
            Tuple.Create(BYBIT, 103),
            Tuple.Create(BITGET, 104),
            // Add other exchanges here as needed
        };

    // Static constructor to initialize our maps from HardcodedExchanges
    static Exchange()
    {
        foreach (var exchange in HardcodedExchanges)
        {
            // Ensure unique market names and identifiers during initialization
            if (Exchanges.ContainsKey(exchange.Item1))
            {
                throw new InvalidOperationException($"Duplicate exchange name '{exchange.Item1}' found in hardcoded list.");
            }
            if (ReverseExchanges.ContainsKey(exchange.Item2))
            {
                throw new InvalidOperationException($"Duplicate exchange identifier '{exchange.Item2}' found in hardcoded list.");
            }

            Exchanges[exchange.Item1] = exchange.Item2;
            ReverseExchanges[exchange.Item2] = exchange.Item1;
        }
    }

    public const string BINANCE = "binance";

    public const string BITMEX = "bitmex";

    public const string BYBIT = "bybit";

    public const string BITGET = "bitget";
}
