using System;
using OpenHFT.Core.Api;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;

namespace OpenHFT.Core.Interfaces;

/// <summary>
/// Defines the contract for an order gateway, which is responsible for
/// sending order-related commands to a specific exchange.
/// </summary>
public interface IOrderGateway
{
    ExchangeEnum SourceExchange { get; }
    ProductType ProdType { get; }
    /// <summary>
    /// Indicates whether the exchange supports atomic Cancel/Replace (Amend) operations.
    /// If false, the Order object must emulate replacement via Cancel + New.
    /// </summary>
    bool SupportsOrderReplacement { get; }
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

    /// <summary>
    /// Fetches the current status of an order from the exchange via REST API.
    /// </summary>
    Task<RestApiResult<OrderStatusReport>> FetchOrderStatusAsync(string exchangeOrderId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Cancels all open orders for a specific symbol.
    /// Primarily used for cleanup in integration tests or emergency stops.
    /// </summary>
    Task CancelAllOrdersAsync(string symbol, CancellationToken cancellationToken = default);
    /// <summary>
    /// Submits a request to cancel multiple existing orders in a single batch.
    /// </summary>
    /// <returns>A list of result objects, one for each order cancellation attempt.</returns>
    Task<IReadOnlyList<OrderModificationResult>> SendBulkCancelOrdersAsync(BulkCancelOrdersRequest request, CancellationToken cancellationToken = default);
}