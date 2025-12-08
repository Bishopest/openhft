using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Api;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;

namespace OpenHFT.Gateway;

/// <summary>
/// A decorator for an IOrderGateway that enforces rate limits by immediately rejecting requests
/// that exceed the configured threshold. It does not queue or delay requests.
/// </summary>
public sealed class ThrottlingGatewayDecorator : IOrderGateway
{
    private readonly IOrderGateway _wrappedGateway;
    private readonly ILogger<ThrottlingGatewayDecorator> _logger;
    private readonly RateLimiter _perSecondLimiter;
    private readonly RateLimiter _perMinuteLimiter;

    public ExchangeEnum SourceExchange => _wrappedGateway.SourceExchange;
    public ProductType ProdType => _wrappedGateway.ProdType;

    public ThrottlingGatewayDecorator(
        IOrderGateway wrappedGateway,
        ILogger<ThrottlingGatewayDecorator> logger,
        RateLimiterConfig perSecondConfig,
        RateLimiterConfig perMinuteConfig)
    {
        _wrappedGateway = wrappedGateway;
        _logger = logger;
        _perSecondLimiter = new RateLimiter(perSecondConfig);
        _perMinuteLimiter = new RateLimiter(perMinuteConfig);
    }

    public Task<OrderPlacementResult> SendNewOrderAsync(NewOrderRequest request, CancellationToken cancellationToken = default)
    {
        if (_perSecondLimiter.TryAcquireToken() && _perMinuteLimiter.TryAcquireToken())
        {
            return _wrappedGateway.SendNewOrderAsync(request, cancellationToken);
        }

        _logger.LogWarningWithCaller($"Rate limit exceeded. Rejecting new order request for ClientOrderId {request.ClientOrderId}.");
        var rejectionResult = new OrderPlacementResult(false, null, "Rate limit exceeded.");
        return Task.FromResult(rejectionResult);
    }

    public Task<OrderModificationResult> SendReplaceOrderAsync(ReplaceOrderRequest request, CancellationToken cancellationToken = default)
    {
        if (_perSecondLimiter.TryAcquireToken() && _perMinuteLimiter.TryAcquireToken())
        {
            return _wrappedGateway.SendReplaceOrderAsync(request, cancellationToken);
        }

        _logger.LogWarningWithCaller($"Rate limit exceeded. Rejecting replace order request for ExchangeOrderId {request.OrderId}.");
        var rejectionResult = new OrderModificationResult(false, request.OrderId, "Rate limit exceeded.");
        return Task.FromResult(rejectionResult);
    }

    public Task<OrderModificationResult> SendCancelOrderAsync(CancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        if (_perSecondLimiter.TryAcquireToken() && _perMinuteLimiter.TryAcquireToken())
        {
            return _wrappedGateway.SendCancelOrderAsync(request, cancellationToken);
        }

        _logger.LogWarningWithCaller($"Rate limit exceeded. Rejecting cancel order request for ExchangeOrderId {request.OrderId}.");
        var rejectionResult = new OrderModificationResult(false, request.OrderId, "Rate limit exceeded.");
        return Task.FromResult(rejectionResult);
    }

    public Task<RestApiResult<OrderStatusReport>> FetchOrderStatusAsync(string exchangeOrderId, CancellationToken cancellationToken = default)
    {
        _logger.LogWarningWithCaller($"Bypassing rate limiter for critical FetchOrderStatusAsync on OrderId {exchangeOrderId}.");
        return _wrappedGateway.FetchOrderStatusAsync(exchangeOrderId, cancellationToken);
    }

    /// <summary>
    /// Applies rate limiting to a bulk cancel request. If the limit is exceeded,
    /// it immediately returns a failure result for all requested orders.
    /// </summary>
    public Task<IReadOnlyList<OrderModificationResult>> SendBulkCancelOrdersAsync(BulkCancelOrdersRequest request, CancellationToken cancellationToken = default)
    {
        if (_perSecondLimiter.TryAcquireToken() && _perMinuteLimiter.TryAcquireToken())
        {
            // Rate limit token acquired. Pass the request directly.
            return _wrappedGateway.SendBulkCancelOrdersAsync(request, cancellationToken);
        }

        // Rate limit exceeded. Immediately return a failure result for each order in the request.
        _logger.LogWarning("Rate limit exceeded. Rejecting bulk cancel request for {Count} orders on instrument {InstrumentId}.",
            request.ExchangeOrderIds.Count, request.InstrumentId);

        var rejectionResults = request.ExchangeOrderIds
            .Select(id => new OrderModificationResult(false, id, "Rate limit exceeded."))
            .ToList();

        return Task.FromResult<IReadOnlyList<OrderModificationResult>>(rejectionResults);
    }

    public Task CancelAllOrdersAsync(string symbol, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Bypassing rate limiter for critical CancelAllOrdersAsync on symbol {Symbol}.", symbol);
        return _wrappedGateway.CancelAllOrdersAsync(symbol, cancellationToken);
    }
}