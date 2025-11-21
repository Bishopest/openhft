using System;
using System.Reflection.Metadata.Ecma335;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Instruments;

public class CryptoPerpetual : CryptoFuture
{
    public CryptoPerpetual(
        int instrumentId,
        string symbol,
        ExchangeEnum exchange,
        Currency baseCurrency,
        Currency quoteCurrency,
        Price tickSize,
        Quantity lotSize,
        decimal multiplier,
        Quantity minOrderSize)
        : base(instrumentId, symbol, exchange, baseCurrency, quoteCurrency, tickSize, lotSize, multiplier, minOrderSize)
    {
    }

    public override ProductType ProductType => ProductType.PerpetualFuture;
    public override string ToString() => $"{Symbol}-PERP on {SourceExchange}";
}
