namespace OpenHFT.Core.Models;

/// <summary>
/// Order side enumeration
/// </summary>
public enum Side : byte
{
    Buy = 0,
    Sell = 1
}

/// <summary>
/// Market data event types
/// </summary>
public enum EventKind : byte
{
    Add = 0,        // New price level or order
    Update = 1,     // Quantity change at existing level
    Delete = 2,     // Remove price level or order
    Trade = 3,      // Trade execution
    Snapshot = 4    // Full book snapshot
}

/// <summary>
/// Order types
/// </summary>
public enum OrderTypeEnum : byte
{
    Limit = 0,
    Market = 1,
    Stop = 2,
    StopLimit = 3
}

/// <summary>
/// Acknowledgment types from gateway
/// </summary>
public enum AckKind : byte
{
    Accepted = 0,
    Rejected = 1,
    CancelAccepted = 2,
    CancelRejected = 3,
    ReplaceAccepted = 4,
    ReplaceRejected = 5,
    Filled = 6,
    PartialFill = 7,
    Cancelled = 8,
    Expired = 9
}

/// <summary>
/// Order status tracking
/// </summary>
public enum OrderStatusEnum : byte
{
    PendingNew = 0,
    New = 1,
    PartiallyFilled = 2,
    Filled = 3,
    DoneForDay = 4,
    Cancelled = 5,
    Replaced = 6,
    PendingCancel = 7,
    Stopped = 8,
    Rejected = 9,
    Suspended = 10,
    PendingNew_PendingReplace = 11,
    Calculated = 12,
    Expired = 13,
    AcceptedForBidding = 14,
    PendingReplace = 15
}

/// <summary>
/// Time in force options
/// </summary>
public enum TimeInForce : byte
{
    Day = 0,
    GTC = 1,        // Good Till Cancelled
    IOC = 2,        // Immediate or Cancel
    FOK = 3,        // Fill or Kill
    GTD = 4         // Good Till Date
}
