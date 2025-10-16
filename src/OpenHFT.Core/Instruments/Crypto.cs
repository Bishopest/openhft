using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Instruments;

public class Crypto : Instrument
{
    public Crypto(
        int instrumentId,
        string symbol,
        ExchangeEnum exchange,
        Currency baseCurrency,
        Currency quoteCurrency,
        Price tickSize,
        Quantity lotSize,
        Quantity minOrderSize
    ) : base(instrumentId, symbol, exchange, baseCurrency, quoteCurrency, tickSize, lotSize, minOrderSize)
    {

    }

    public override ProductType ProductType => ProductType.Spot;
}
