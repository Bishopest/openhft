using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Instruments;

public class CryptoPerpetual : CryptoFuture
{
    public CryptoPerpetual(
        long instrumentId,
        string symbol,
        ExchangeEnum exchange,
        Currency baseCurrency,
        Currency quoteCurrency,
        decimal tickSize,
        decimal lotSize,
        decimal multiplier)
        : base(instrumentId, symbol, exchange, baseCurrency, quoteCurrency, tickSize, lotSize, multiplier)
    {
    }

    public override ProductType ProductType => ProductType.PerpetualFuture;

    public override string ToString() => $"{Symbol}-PERP on {Exchange}";

}
