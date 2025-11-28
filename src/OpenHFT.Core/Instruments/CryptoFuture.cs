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

    public override CurrencyAmount ValueInDenominationCurrency(Price p, Quantity orderQty)
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
                return base.BaseCurrency == Currency.BTC ? CurrencyAmount.FromDecimal(1 / p.ToDecimal() * orderQty.ToDecimal() * Multiplier, Currency.BTC) :
                                                           CurrencyAmount.FromDecimal(p.ToDecimal() * orderQty.ToDecimal() * Multiplier, Currency.BTC);
            }
            else
            {
                return CurrencyAmount.FromDecimal(1 / p.ToDecimal() * orderQty.ToDecimal() * Multiplier, DenominationCurrency);
            }
        }
        else
        {
            return CurrencyAmount.FromDecimal(p.ToDecimal() * orderQty.ToDecimal() * Multiplier, DenominationCurrency);
        }
    }

    public override Price PriceFromValue(CurrencyAmount value, Quantity orderQty)
    {
        if (orderQty.ToDecimal() == 0m)
        {
            return Price.FromDecimal(0m);
        }

        decimal pDecimal;

        // Multiplier가 0일 경우에 대한 방어 코드가 필요하다면 추가 (보통 Instrument 생성 시 0이 아님이 보장됨)
        decimal qty = orderQty.ToDecimal();
        decimal val = value.Amount;

        // 로직 분기: ValueInDenominationCurrency의 역연산 수행

        if (base.QuoteCurrency == Currency.USD)
        {
            var isBitmex = base.SourceExchange == ExchangeEnum.BITMEX;
            if (isBitmex)
            {
                if (base.BaseCurrency == Currency.BTC)
                {
                    // Case 1: BitMEX XBTUSD (Inverse)
                    // Formula: Value = (1 / P) * Qty * Multiplier
                    // => P = (Qty * Multiplier) / Value
                    if (val == 0m) return Price.FromDecimal(0m);
                    pDecimal = (qty * Multiplier) / val;
                }
                else
                {
                    // Case 2: BitMEX Linear (if any, e.g., ETHUSD where quote is USD but payout is XBT? - Usually Bitmex Linear is Quanto)
                    // Formula: Value = P * Qty * Multiplier
                    // => P = Value / (Qty * Multiplier)
                    pDecimal = val / (qty * Multiplier);
                }
            }
            else
            {
                // Case 3: Other Inverse Contracts (e.g. Binance Coin-M)
                // Formula: Value = (1 / P) * Qty * Multiplier
                // => P = (Qty * Multiplier) / Value
                if (val == 0m) return Price.FromDecimal(0m);
                pDecimal = (qty * Multiplier) / val;
            }
        }
        else
        {
            // Case 4: Linear Contracts (e.g. BTCUSDT)
            // Formula: Value = P * Qty * Multiplier
            // => P = Value / (Qty * Multiplier)
            pDecimal = val / (qty * Multiplier);
        }

        // 가격이 음수이거나 무한대일 경우 0 처리 (혹은 예외)
        if (pDecimal <= 0m)
        {
            return Price.FromDecimal(0m);
        }

        return Price.FromDecimal(pDecimal);
    }



    // currency unit in value accounting
    public override Currency DenominationCurrency => GetDenominationCurrency();

    private Currency GetDenominationCurrency()
    {
        if (base.QuoteCurrency == Currency.USD)
        {
            var isBitmex = base.SourceExchange == ExchangeEnum.BITMEX;
            if (isBitmex) return Currency.BTC;

            return Currency.USDT;
        }

        return base.QuoteCurrency;
    }
}