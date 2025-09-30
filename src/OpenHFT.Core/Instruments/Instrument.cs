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
    public long InstrumentId { get; }

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
    public ExchangeEnum Exchange { get; }

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
    public decimal TickSize { get; }

    /// <summary>
    /// The smallest quantity change allowed (e.g., 0.001 for BTC).
    /// </summary>
    public decimal LotSize { get; }

    protected Instrument(
        long instrumentId,
        string symbol,
        ExchangeEnum exchange,
        Currency baseCurrency,
        Currency quoteCurrency,
        decimal tickSize,
        decimal lotSize)
    {
        InstrumentId = instrumentId;
        Symbol = symbol;
        Exchange = exchange;
        BaseCurrency = baseCurrency;
        QuoteCurrency = quoteCurrency;
        TickSize = tickSize;
        LotSize = lotSize;
    }

    public override string ToString() => $"{Symbol} on {Exchange}";
}
