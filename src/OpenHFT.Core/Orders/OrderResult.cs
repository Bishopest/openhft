using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Orders;

/// <summary>
/// The result of an order placement attempt.
/// </summary>
public readonly struct OrderPlacementResult
{
    public readonly bool IsSuccess { get; }
    public readonly long ClientOrderId { get; }
    public readonly string? FailureReason { get; }
    // The initial OrderStatusReport can be included here if the exchange provides one immediately.
    public readonly OrderStatusReport? InitialReport { get; }

    public OrderPlacementResult(bool isSuccess, long clientOrderId, string? failureReason = null, OrderStatusReport? initialReport = null)
    {
        IsSuccess = isSuccess;
        ClientOrderId = clientOrderId;
        FailureReason = failureReason;
        InitialReport = initialReport;
    }
}

/// <summary>
/// The result of an order modification (cancel/replace) attempt.
/// </summary>
public readonly struct OrderModificationResult
{
    public readonly bool IsSuccess { get; }
    public readonly long ClientOrderId { get; }
    public readonly string? FailureReason { get; }

    public OrderModificationResult(bool isSuccess, long clientOrderId, string? failureReason = null)
    {
        IsSuccess = isSuccess;
        ClientOrderId = clientOrderId;
        FailureReason = failureReason;
    }
}