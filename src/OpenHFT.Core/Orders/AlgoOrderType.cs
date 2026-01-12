namespace OpenHFT.Core.Orders;

public enum AlgoOrderType
{
    None,
    /// <summary>
    /// If hedge side bid, then ask first price 
    /// </summary>
    OppositeFirst,

    /// <summary>
    /// If hedge side bid, then +1 tick from the best bid
    /// </summary>
    FirstFollow
}