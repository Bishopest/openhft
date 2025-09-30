namespace OpenHFT.Core.Instruments;

/// <summary>
/// Defines the type of financial product or market segment.
/// </summary>
public enum ProductType
{
    /// <summary>
    /// Spot market (e.g., BTC/USDT).
    /// </summary>
    Spot,

    /// <summary>
    /// Perpetual Futures (e.g., BTCUSDT-PERP).
    /// </summary>
    PerpetualFuture,

    /// <summary>
    /// Futures with a specific expiration date.
    /// </summary>
    DatedFuture,

    /// <summary>
    /// Options market.
    /// </summary>
    Option

}