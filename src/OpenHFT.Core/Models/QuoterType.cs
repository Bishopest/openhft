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
    /// A quoter that make trend-following quote by making reverse quote
    /// </summary>
    Trend,
}
