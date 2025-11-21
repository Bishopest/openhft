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
        int instrumentId,
        string symbol,
        ExchangeEnum exchange,
        Currency baseCurrency,
        Currency quoteCurrency,
        Price tickSize,
        Quantity lotSize,
        decimal multiplier,
        Quantity minOrderSize)
        : base(instrumentId, symbol, exchange, baseCurrency, quoteCurrency, tickSize, lotSize, minOrderSize)
    {
        Multiplier = multiplier;
    }

    public override CurrencyAmount ValueInDenominationCurrency(Price p, Quantity q)
    {
        if (p == Price.FromDecimal(0m))
        {
            return CurrencyAmount.FromDecimal(0m, DenominationCurrency);
        }

        if (base.QuoteCurrency == Currency.USD)
        {
            var isBitmex = base.SourceExchange == ExchangeEnum.BITMEX;
            if (isBitmex)
            {
                return base.BaseCurrency == Currency.BTC ? CurrencyAmount.FromDecimal(1 / p.ToDecimal() * q.ToDecimal() * Multiplier, Currency.BTC) :
                                                           CurrencyAmount.FromDecimal(p.ToDecimal() * q.ToDecimal() * Multiplier, Currency.BTC);
            }
            else
            {
                return CurrencyAmount.FromDecimal(1 / p.ToDecimal() * q.ToDecimal() * Multiplier, DenominationCurrency);
            }
        }
        else
        {
            return CurrencyAmount.FromDecimal(p.ToDecimal() * q.ToDecimal() * Multiplier, DenominationCurrency);
        }
    }

    public override Currency DenominationCurrency => GetDenominationCurrency();

    private Currency GetDenominationCurrency()
    {
        if (base.QuoteCurrency == Currency.USD)
        {
            var isBitmex = base.SourceExchange == ExchangeEnum.BITMEX;
            if (isBitmex) return Currency.BTC;

            return base.BaseCurrency;
        }

        return base.QuoteCurrency;
    }
}