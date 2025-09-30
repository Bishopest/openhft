using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Instruments;

public class Crypto : Instrument
{
    public Crypto(
        long instrumentId,
        string symbol,
        ExchangeEnum exchange,
        Currency baseCurrency,
        Currency quoteCurrency,
        decimal tickSize,
        decimal lotSize
    ) : base(instrumentId, symbol, exchange, baseCurrency, quoteCurrency, tickSize, lotSize)
    {

    }

    public override ProductType ProductType => ProductType.Spot;
}
