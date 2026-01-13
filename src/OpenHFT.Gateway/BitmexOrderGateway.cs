using System;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Api;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;
using OpenHFT.Gateway.ApiClient;
using OpenHFT.Gateway.ApiClient.Bitmex;

namespace OpenHFT.Gateway;

public class BitmexOrderGateway : IOrderGateway
{
    private readonly ILogger<BitmexOrderGateway> _logger;
    private readonly BitmexRestApiClient _apiClient;
    private readonly IInstrumentRepository _instrumentRepository;

    public bool SupportsOrderReplacement { get; } = true;
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

        var replaceReport = MapResponseToReport(result.Data, request.InstrumentId);
        return new OrderModificationResult(true, result.Data.OrderId, report: replaceReport);
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

        if (!result.Data.Any())
        {
            _logger.LogWarningWithCaller($"No cancel responses on instrument id {request.InstrumentId}");
            return new OrderModificationResult(false, request.OrderId, $"No cancel responses on instrument id {request.InstrumentId}");
        }

        var cancelReport = MapResponseToReport(result.Data.FirstOrDefault(), request.InstrumentId);
        return new OrderModificationResult(true, request.OrderId, report: cancelReport);
    }

    public async Task<IReadOnlyList<OrderModificationResult>> SendBulkCancelOrdersAsync(BulkCancelOrdersRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ExchangeOrderIds == null || !request.ExchangeOrderIds.Any())
        {
            return Array.Empty<OrderModificationResult>();
        }

        var payload = new Dictionary<string, object> { { "orderID", request.ExchangeOrderIds } };

        var result = await _apiClient.SendPrivateRequestAsync<List<BitmexOrderResponse>>(
            HttpMethod.Delete, _apiClient.ExecutionMode == ExecutionMode.Realtime ? "/api/v2/order" : "/api/v1/order", queryParams: null, bodyParams: payload, cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarningWithCaller($"Bulk cancel API call failed: {result.Error.Message}");
            // If the whole request fails, create a failure result for each requested order.
            return request.ExchangeOrderIds
                .Select(id => new OrderModificationResult(false, id, result.Error.Message))
                .ToList();
        }

        var results = new List<OrderModificationResult>();
        var responseMap = result.Data.ToDictionary(r => r.OrderId, r => r);

        // Correlate requests with responses
        foreach (var requestedId in request.ExchangeOrderIds)
        {
            if (responseMap.TryGetValue(requestedId, out var response))
            {
                // Successful cancellation for this specific order
                var report = MapResponseToReport(response, request.InstrumentId);
                results.Add(new OrderModificationResult(true, requestedId, report: report));
            }
            else
            {
                // The response did not contain an entry for this order, assume failure.
                // This can happen if the order was already filled/cancelled.
                results.Add(new OrderModificationResult(false, requestedId, "Order not found in bulk cancel response."));
            }
        }

        return results;
    }

    public async Task<RestApiResult<OrderStatusReport>> FetchOrderStatusAsync(string exchangeOrderId, CancellationToken cancellationToken = default)
    {
        // BitMEX는 clOrdID로 주문을 필터링할 수 있습니다.
        var filter = JsonSerializer.Serialize(new { orderID = exchangeOrderId });
        var encodedFilter = Uri.EscapeDataString(filter);
        var version = _apiClient.ExecutionMode == ExecutionMode.Realtime ? "/api/v2/order" : "/api/v1/order";
        var endpoint = $"{version}?filter={encodedFilter}";

        var apiResult = await _apiClient.SendPrivateRequestAsync<List<BitmexOrderResponse>>(
            HttpMethod.Get, endpoint, cancellationToken: cancellationToken);

        if (!apiResult.IsSuccess)
        {
            return RestApiResult<OrderStatusReport>.Failure(apiResult.Error);
        }

        var orderResponse = apiResult.Data.FirstOrDefault();
        if (orderResponse == null)
        {
            var error = new RestApiException("Order not found on exchange.", System.Net.HttpStatusCode.NotFound, null);
            return RestApiResult<OrderStatusReport>.Failure(error);
        }

        var instrument = _instrumentRepository.FindBySymbol(orderResponse.Symbol, this.ProdType, this.SourceExchange);
        if (instrument == null)
        {
            var error = new RestApiException("Instrument not found for fetched order.", System.Net.HttpStatusCode.NotFound, null);
            return RestApiResult<OrderStatusReport>.Failure(error);
        }

        var report = MapResponseToReport(orderResponse, instrument.InstrumentId);
        return report is null ? RestApiResult<OrderStatusReport>.Failure(new RestApiException("Failed to map order response.", System.Net.HttpStatusCode.InternalServerError, null)) : RestApiResult<OrderStatusReport>.Success(report.Value);
    }

    // Helper to map exchange-specific response to our standard DTO
    private OrderStatusReport? MapResponseToReport(BitmexOrderResponse response, int instrumentId)
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

        if (status == OrderStatus.Pending)
        {
            _logger.LogWarningWithCaller($"Can not map status({response.OrdStatus}), return null");
            return null;
        }

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
