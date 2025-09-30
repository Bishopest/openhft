using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Instruments;

/// <summary>
/// Abstract base class for all cryptocurrency future instruments.
/// </summary>
public abstract class CryptoFuture : Instrument
{
    /// <summary>
    /// The contract value multiplier. For example, if 1 contract is for 0.001 BTC, the multiplier is 0.001.
    /// It defines the quantity of the base currency per contract.
    /// </summary>
    public decimal Multiplier { get; }

    protected CryptoFuture(
        long instrumentId,
        string symbol,
        ExchangeEnum exchange,
        Currency baseCurrency,
        Currency quoteCurrency,
        decimal tickSize,
        decimal lotSize,
        decimal multiplier)
        : base(instrumentId, symbol, exchange, baseCurrency, quoteCurrency, tickSize, lotSize)
    {
        Multiplier = multiplier;
    }
}