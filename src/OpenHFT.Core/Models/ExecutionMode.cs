namespace OpenHFT.Core.Models;

public enum ExecutionMode
{
    /// <summary>
    /// Live trading with low latency endpoint
    /// </summary>
    Realtime,
    /// <summary>
    /// Live trading with real funds.
    /// </summary>
    Live,

    /// <summary>
    /// Trading on a test network with paper money.
    /// </summary>
    Testnet
}
