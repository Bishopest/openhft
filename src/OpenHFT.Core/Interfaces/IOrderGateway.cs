using System;
using OpenHFT.Core.Orders;

namespace OpenHFT.Core.Interfaces;

/// <summary>
/// Defines the contract for an order gateway, which is responsible for
/// sending order-related commands to a specific exchange.
/// </summary>
public interface IOrderGateway
{
    /// <summary>
    /// Submits a new order to the exchange.
    /// </summary>
    /// <param name="request">An immutable object containing all necessary information for the new order.</param>
    /// <param name="cancellationToken">A token for cancelling the request.</param>
    /// <returns>A result object indicating the outcome of the placement attempt.</returns>
    Task<OrderPlacementResult> SendNewOrderAsync(NewOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a request to replace an existing order.
    /// </summary>
    /// <param name="request">An immutable object containing the details for the replacement.</param>
    /// <param name="cancellationToken">A token for cancelling the request.</param>
    /// <returns>A result object indicating the outcome of the replacement attempt.</returns>
    Task<OrderModificationResult> SendReplaceOrderAsync(ReplaceOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a request to cancel an existing order.
    /// </summary>
    /// <param name="request">An immutable object containing the identifiers for the order to be cancelled.</param>
    /// <param name="cancellationToken">A token for cancelling the request.</param>
    /// <returns>A result object indicating the outcome of the cancellation attempt.</returns>
    Task<OrderModificationResult> SendCancelOrderAsync(CancelOrderRequest request, CancellationToken cancellationToken = default);
}