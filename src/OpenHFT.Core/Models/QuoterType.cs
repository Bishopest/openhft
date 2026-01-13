namespace OpenHFT.Core.Models;

public enum QuoterType
{
    /// <summary>
    /// A mock quoter that only logs actions to the console/logger.
    /// </summary>
    Log,

    /// <summary>
    /// A quoter that submits single order to the exchange.
    /// </summary>
    Single,

    /// <summary>
    /// A quoter that submits multiple orders by layeredQuoteManager structur./
    /// </summary>
    Layered,

    /// <summary>
    /// A quoter that aggressively hits marketable prices and cancels remaining orders not filled right away(similar to IOC)
    /// </summary>
    Shadow
}
