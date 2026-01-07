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
    /// A quoter that submits multiple orders by ordersOnGroup structure.
    /// </summary>
    Multi,

    /// <summary>
    /// A quoter that submits multiple orders by layeredQuoteManager structur./
    /// </summary>
    Layered,

    /// <summary>
    /// A quoter that aggressively hits marketable prices and cancels remaining orders not filled right away(similar to IOC)
    /// </summary>
    Shadow,
    /// <summary>
    /// A quoter that aggressively hits marketable prices and remains on the book to capture maker rebates. 
    /// It continuously monitors the order book and cancels the remaining quantity if the order loses price priority (is outquoted).
    /// </summary>
    ShadowMaker
}
