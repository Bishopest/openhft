using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Gateway.ApiClient;
using OpenHFT.Gateway.ApiClient.Bitmex;
using OpenHFT.Gateway.ApiClient.Exceptions;

namespace OpenHFT.Gateway;

public class BitmexOrderGateway : IOrderGateway
{
    private readonly BitmexRestApiClient _apiClient;
    private readonly IInstrumentRepository _instrumentRepository;

    public ExchangeEnum SourceExchange => ExchangeEnum.BITMEX;
    public ProductType ProdType { get; }

    public BitmexOrderGateway(
        BitmexRestApiClient apiClient,
        IInstrumentRepository instrumentRepository,
        ProductType prodType)
    {
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
            { "clOrdID", request.ClientOrderId.ToString() }
        };

        try
        {
            var response = await _apiClient.SendPrivateRequestAsync<BitmexOrderResponse>(
                HttpMethod.Post, "/api/v1/order", queryParams: null, bodyParams: payload, cancellationToken);

            var initialReport = MapResponseToReport(response, instrument.InstrumentId);
            return new OrderPlacementResult(true, response.OrderId, initialReport: initialReport);
        }
        catch (RestApiException ex)
        {
            return new OrderPlacementResult(false, null, $"API Error: {ex.StatusCode} - {ex.ResponseContent}");
        }
    }

    public async Task<OrderModificationResult> SendReplaceOrderAsync(ReplaceOrderRequest request, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object>
        {
            { "orderID", request.OrderId },
            { "price", request.NewPrice.ToDecimal() },
        };

        try
        {
            var response = await _apiClient.SendPrivateRequestAsync<BitmexOrderResponse>(
                HttpMethod.Put, "/api/v1/order", queryParams: null, bodyParams: payload, cancellationToken);
            return new OrderModificationResult(true, response.OrderId);
        }
        catch (RestApiException ex)
        {
            return new OrderModificationResult(false, request.OrderId, $"API Error: {ex.StatusCode} - {ex.ResponseContent}");
        }
    }

    public async Task<OrderModificationResult> SendCancelOrderAsync(CancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object> { { "orderID", request.OrderId } };

        try
        {
            // BitMEX 취소 응답은 배열 형태
            var response = await _apiClient.SendPrivateRequestAsync<List<BitmexOrderResponse>>(
                HttpMethod.Delete, "/api/v1/order", queryParams: null, bodyParams: payload, cancellationToken);

            return new OrderModificationResult(true, request.OrderId);
        }
        catch (RestApiException ex)
        {
            return new OrderModificationResult(false, request.OrderId, $"API Error: {ex.StatusCode} - {ex.ResponseContent}");
        }
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
            instrumentId: instrumentId,
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
            HttpMethod.Delete, "/api/v1/order/all", bodyParams: payload, cancellationToken: cancellationToken);
    }
}
