using System;

namespace OpenHFT.Quoting.Orders.Models;

public enum OrderStatus
{
    Pending,
    NewRequest,
    ReplaceRequest,
    CancelRequest,
    New,
    Cancelled,
    Rejected,
    Filled,
    PartiallyFilled
}