namespace OpenHFT.Hedging;

public enum HedgeOrderType
{
    /// <summary>
    /// If hedge side bid, then bid first price and do not replace until position expired by quoter.
    /// </summary>
    AnchorInner,
    /// <summary>
    /// If hedge side bid, then ask first price 
    /// </summary>
    OppositeFirst,

    /// <summary>
    /// If hedge side bid, then +1 tick from the best bid
    /// </summary>
    FirstFollow
}
