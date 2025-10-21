using System;

namespace OpenHFT.Core.Models;

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