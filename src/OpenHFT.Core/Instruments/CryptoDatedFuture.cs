using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Instruments;

/// <summary>
/// Represents a future with a specific expiration date.
/// </summary>
public class CryptoDatedFuture : CryptoFuture
{
    /// <summary>
    /// The date and time when the future contract expires.
    /// </summary>
    public DateTimeOffset ExpirationDate { get; }


    public CryptoDatedFuture(
        int instrumentId,
        string symbol,
        ExchangeEnum exchange,
        Currency baseCurrency,
        Currency quoteCurrency,
        decimal tickSize,
        decimal lotSize,
        decimal multiplier,
        decimal minOrderSize,
        DateTimeOffset expirationDate)
        : base(instrumentId, symbol, exchange, baseCurrency, quoteCurrency, tickSize, lotSize, multiplier, minOrderSize)
    {
        ExpirationDate = expirationDate;
    }

    public override ProductType ProductType => ProductType.DatedFuture;
    public override string ToString() => $"{Symbol}-{ExpirationDate:yyMMdd} on {Exchange}";
}
