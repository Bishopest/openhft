using System;
using OpenHFT.Core.Api;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;

namespace OpenHFT.Gateway;

public class NullOrderGateway : IOrderGateway
{
    public bool SupportsOrderReplacement { get; } = false;
    public ExchangeEnum SourceExchange => ExchangeEnum.Undefined;
    public ProductType ProdType => ProductType.PerpetualFuture;

    public Task CancelAllOrdersAsync(string symbol, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RestApiResult<OrderStatusReport>> FetchOrderStatusAsync(string exchangeOrderId, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<IReadOnlyList<OrderModificationResult>> SendBulkCancelOrdersAsync(BulkCancelOrdersRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<OrderModificationResult>>(new List<OrderModificationResult>().AsReadOnly());
    }

    public Task<OrderModificationResult> SendCancelOrderAsync(CancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new OrderModificationResult());
    }

    public Task<OrderPlacementResult> SendNewOrderAsync(NewOrderRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new OrderPlacementResult());
    }

    public Task<OrderModificationResult> SendReplaceOrderAsync(ReplaceOrderRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new OrderModificationResult());
    }
}
