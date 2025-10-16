using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Instruments;

/// <summary>
/// Abstract base class for all financial instruments.
/// </summary>
public abstract class Instrument
{
    /// <summary>
    /// Unique internal identifier for the instrument.
    /// </summary>
    public int InstrumentId { get; }

    /// <summary>
    /// Gets the specific product type of this instrument.
    /// </summary>
    public abstract ProductType ProductType { get; }

    /// <summary>
    /// The exchange-specific symbol (e.g., "BTCUSDT").
    /// </summary>
    public string Symbol { get; }

    /// <summary>
    /// The exchange where this instrument is traded.
    /// </summary>
    public ExchangeEnum SourceExchange { get; }

    /// <summary>
    /// The asset being traded (e.g., BTC in BTC/USDT).
    /// </summary>
    public Currency BaseCurrency { get; }

    /// <summary>
    /// The currency in which the instrument is priced (e.g., USDT in BTC/USDT).
    /// </summary>
    public Currency QuoteCurrency { get; }

    /// <summary>
    /// The smallest price change allowed (e.g., 0.01 for BTCUSDT).
    /// </summary>
    public Price TickSize { get; }

    /// <summary>
    /// The smallest quantity change allowed (e.g., 0.001 for BTC).
    /// </summary>
    public Quantity LotSize { get; }

    /// <summary>
    /// The smallest quantity allowed to send order with
    /// </summary>
    public Quantity MinOrderSize { get; }

    protected Instrument(
        int instrumentId,
        string symbol,
        ExchangeEnum exchange,
        Currency baseCurrency,
        Currency quoteCurrency,
        Price tickSize,
        Quantity lotSize,
        Quantity minOrderSize)
    {
        InstrumentId = instrumentId;
        Symbol = symbol;
        SourceExchange = exchange;
        BaseCurrency = baseCurrency;
        QuoteCurrency = quoteCurrency;
        TickSize = tickSize;
        LotSize = lotSize;
        MinOrderSize = minOrderSize;
    }

    public override string ToString() => $"{Symbol} on {SourceExchange}";
}
