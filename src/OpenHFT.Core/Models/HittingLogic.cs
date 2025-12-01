namespace OpenHFT.Core.Models;

public enum HittingLogic
{
    /// <summary>
    /// allow quoter to quote on any prices
    /// </summary>
    AllowAll,
    /// <summary>
    /// prevent quoter to quote price over/under best price on orderbook when buy/sell
    /// </summary>
    OurBest,
    /// <summary>
    /// make quoter to quote price over/under just +1 tick from best price on orderbook when buy/sell
    /// only when our quote cross the best oprice
    /// </summary>
    Pennying
}
