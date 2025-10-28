using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;

namespace OpenHFT.Gateway;

public class NullOrderGateway : IOrderGateway
{
    public ExchangeEnum SourceExchange => ExchangeEnum.Undefined;

    public ProductType ProdType => ProductType.PerpetualFuture;

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
