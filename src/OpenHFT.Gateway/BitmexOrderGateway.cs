using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;
using OpenHFT.Gateway.ApiClient;
using OpenHFT.Gateway.ApiClient.Bitmex;
using OpenHFT.Gateway.ApiClient.Exceptions;

namespace OpenHFT.Gateway;

public class BitmexOrderGateway : IOrderGateway
{
    private readonly ILogger<BitmexOrderGateway> _logger;
    private readonly BitmexRestApiClient _apiClient;
    private readonly IInstrumentRepository _instrumentRepository;

    public ExchangeEnum SourceExchange => ExchangeEnum.BITMEX;
    public ProductType ProdType { get; }

    public BitmexOrderGateway(
        ILogger<BitmexOrderGateway> logger,
        BitmexRestApiClient apiClient,
        IInstrumentRepository instrumentRepository,
        ProductType prodType)
    {
        _logger = logger;
        _apiClient = apiClient;
        _instrumentRepository = instrumentRepository;
        ProdType = prodType;
    }

    public async Task<OrderPlacementResult> SendNewOrderAsync(NewOrderRequest request, CancellationToken cancellationToken = default)
    {
        var instrument = _instrumentRepository.GetById(request.InstrumentId);
        if (instrument is null)
            return new OrderPlacementResult(false, null, "Instrument not found.");

        var payload = new Dictionary<string, object>
        {
            { "symbol", instrument.Symbol },
            { "side", request.Side.ToString() },
            { "orderQty", request.Quantity.ToDecimal() },
            { "price", request.OrderType == OrderType.Limit ? request.Price.ToDecimal() : null },
            { "ordType", request.OrderType.ToString() },
            { "clOrdID", request.ClientOrderId.ToString() },
        };

        if (request.IsPostOnly)
            payload.Add("execInst", "ParticipateDoNotInitiate");

        var result = await _apiClient.SendPrivateRequestAsync<BitmexOrderResponse>(
            HttpMethod.Post, _apiClient.ExecutionMode == ExecutionMode.Realtime ? "/api/v2/order" : "/api/v1/order", queryParams: null, bodyParams: payload, cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarningWithCaller($"Failed to place order: {result.Error.Message}");
            return new OrderPlacementResult(false, null, result.Error.Message);
        }

        var initialReport = MapResponseToReport(result.Data, instrument.InstrumentId);
        return new OrderPlacementResult(true, result.Data.OrderId, initialReport: initialReport);
    }

    public async Task<OrderModificationResult> SendReplaceOrderAsync(ReplaceOrderRequest request, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object>
        {
            { "orderID", request.OrderId },
            { "price", request.NewPrice.ToDecimal() },
        };

        var result = await _apiClient.SendPrivateRequestAsync<BitmexOrderResponse>(
            HttpMethod.Put, _apiClient.ExecutionMode == ExecutionMode.Realtime ? "/api/v2/order" : "/api/v1/order", queryParams: null, bodyParams: payload, cancellationToken);
        if (!result.IsSuccess)
        {
            _logger.LogWarningWithCaller($"Failed to replace order: {result.Error.Message}");
            return new OrderModificationResult(false, request.OrderId, result.Error.Message);
        }

        return new OrderModificationResult(true, result.Data.OrderId);
    }

    public async Task<OrderModificationResult> SendCancelOrderAsync(CancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object> { { "orderID", request.OrderId } };

        // BitMEX 취소 응답은 배열 형태
        var result = await _apiClient.SendPrivateRequestAsync<List<BitmexOrderResponse>>(
            HttpMethod.Delete, _apiClient.ExecutionMode == ExecutionMode.Realtime ? "/api/v2/order" : "/api/v1/order", queryParams: null, bodyParams: payload, cancellationToken);
        if (!result.IsSuccess)
        {
            _logger.LogWarningWithCaller($"Failed to cancel order: {result.Error.Message}");
            return new OrderModificationResult(false, request.OrderId, result.Error.Message);
        }

        return new OrderModificationResult(true, request.OrderId);
    }

    // Helper to map exchange-specific response to our standard DTO
    private OrderStatusReport MapResponseToReport(BitmexOrderResponse response, int instrumentId)
    {
        var status = response.OrdStatus switch
        {
            "New" => OrderStatus.New,
            "Filled" => OrderStatus.Filled,
            "Canceled" => OrderStatus.Cancelled,
            "Rejected" => OrderStatus.Rejected,
            "PartiallyFilled" => OrderStatus.PartiallyFilled,
            _ => OrderStatus.Pending // Or another default/unknown status
        };

        return new OrderStatusReport(
            clientOrderId: long.TryParse(response.ClOrdId, out var cid) ? cid : 0,
            exchangeOrderId: response.OrderId,
            executionId: null,
            instrumentId: instrumentId,
            side: response.Side == "Buy" ? Side.Buy : Side.Sell,
            status: status,
            price: Price.FromDecimal(response.Price ?? 0),
            quantity: Quantity.FromDecimal(response.OrderQty ?? 0),
            leavesQuantity: Quantity.FromDecimal(response.LeavesQty ?? 0),
            timestamp: ((DateTimeOffset)response.Timestamp).ToUnixTimeMilliseconds()
        );
    }

    public async Task CancelAllOrdersAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object> { { "symbol", symbol } };

        await _apiClient.SendPrivateRequestAsync<List<BitmexOrderResponse>>(
            HttpMethod.Delete, _apiClient.ExecutionMode == ExecutionMode.Realtime ? "/api/v2/order/all" : "/api/v1/order/all", bodyParams: payload, cancellationToken: cancellationToken);
    }
}
