using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

public interface IFxRateService
{
    /// <summary>
    /// Converts an amount from source currency to target currency.
    /// Returns null if conversion rate is not available.
    /// </summary>
    CurrencyAmount? Convert(CurrencyAmount sourceAmount, Currency targetCurrency);
}
