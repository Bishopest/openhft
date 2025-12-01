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
}
