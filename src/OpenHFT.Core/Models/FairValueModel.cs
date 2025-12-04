namespace OpenHFT.Core.Models;

public enum FairValueModel
{
    /// <summary>
    /// Calculates Fair Value as the simple mid-price (average of best bid and best ask).
    /// Highly responsive to market changes but susceptible to noise.
    /// </summary>
    Midp,
    FR,
    BestMidp,
    /// <summary>
    /// Calculates Fair Value based on the Volume-Weighted Average Price (VWAP)
    /// of the each top N levels of the bid and ask sides of the order book.
    /// This model is more stable and less susceptible to noise than the simple mid-price.
    /// </summary>
    VwapMidp,
    OppositeBest
}
