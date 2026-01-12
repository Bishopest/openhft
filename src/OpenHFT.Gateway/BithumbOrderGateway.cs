using System;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Api;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;
using OpenHFT.Gateway.ApiClient;

namespace OpenHFT.Gateway;

public class BithumbOrderGateway : IOrderGateway
{
    private readonly ILogger<BithumbOrderGateway> _logger;
    private readonly BithumbRestApiClient _apiClient;
    private readonly IInstrumentRepository _instrumentRepository;

    public ExchangeEnum SourceExchange => ExchangeEnum.BITHUMB;
    public ProductType ProdType { get; }

    public BithumbOrderGateway(
        ILogger<BithumbOrderGateway> logger,
        BithumbRestApiClient apiClient,
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

        // 빗썸 규격에 맞는 파라미터 변환
        var payload = new Dictionary<string, object>
        {
            { "market", instrument.Symbol },
            { "side", request.Side == Side.Buy ? "bid" : "ask" },
            { "volume", request.Quantity.ToDecimal().ToString() },
            { "price", request.Price.ToDecimal().ToString() },
            { "order_type", MapOrderType(request.OrderType) }
        };

        var result = await _apiClient.SendPrivateRequestAsync<BithumbOrderResponse>(
            HttpMethod.Post, "/v2/orders", queryParams: null, bodyParams: payload, cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarningWithCaller($"Failed to place Bithumb order: {result.Error.Message}");
            return new OrderPlacementResult(false, null, result.Error.Message);
        }

        var initialReport = MapResponseToReport(result.Data, instrument.InstrumentId, request.Side);
        return new OrderPlacementResult(true, result.Data.OrderId, initialReport: initialReport);
    }

    public Task<OrderModificationResult> SendReplaceOrderAsync(ReplaceOrderRequest request, CancellationToken cancellationToken = default)
    {
        // 빗썸은 API 수준에서 주문 정정(Replace/Amend)을 지원하지 않습니다.
        // 필요 시 Cancel 후 Re-entry 로직을 상위 레벨에서 구현해야 합니다.
        _logger.LogWarningWithCaller("Bithumb does not support order replacement.");
        return Task.FromResult(new OrderModificationResult(false, request.OrderId, "Bithumb does not support order replacement."));
    }

    public async Task<OrderModificationResult> SendCancelOrderAsync(CancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        // 빗썸 V2 취소는 DELETE 메서드이며 order_id를 QueryString으로 보냄
        var queryParams = new Dictionary<string, object>
    {
        { "order_id", request.OrderId }
    };

        // 엔드포인트가 /v2/order (단수) 임에 주의
        var result = await _apiClient.SendPrivateRequestAsync<BithumbCancelResponse>(
            HttpMethod.Delete, "/v2/order", queryParams: queryParams, cancellationToken: cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarningWithCaller($"Failed to cancel Bithumb order {request.OrderId}: {result.Error.Message}");
            return new OrderModificationResult(false, request.OrderId, result.Error.Message);
        }

        // 취소 접수 성공 리포트 생성
        var cancelReport = new OrderStatusReport(
            clientOrderId: 0,
            exchangeOrderId: result.Data.OrderId,
            executionId: null,
            instrumentId: request.InstrumentId,
            side: Side.Buy, // 취소 리포트이므로 사이드는 크게 중요치 않으나 필요시 주문 객체에서 참조
            status: OrderStatus.Cancelled,
            price: Price.FromDecimal(0),
            quantity: Quantity.FromDecimal(0),
            leavesQuantity: Quantity.FromDecimal(0),
            timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        return new OrderModificationResult(true, request.OrderId, report: cancelReport);
    }

    public async Task<RestApiResult<OrderStatusReport>> FetchOrderStatusAsync(string exchangeOrderId, CancellationToken cancellationToken = default)
    {
        // 빗썸 V1 조회는 uuid라는 키를 사용함
        var queryParams = new Dictionary<string, object> { { "uuid", exchangeOrderId } };

        var apiResult = await _apiClient.SendPrivateRequestAsync<BithumbOrderLookupResponse>(
            HttpMethod.Get, "/v1/order", queryParams: queryParams, cancellationToken: cancellationToken);

        if (!apiResult.IsSuccess) return RestApiResult<OrderStatusReport>.Failure(apiResult.Error);

        var data = apiResult.Data;
        var inst = _instrumentRepository.FindBySymbol(data.Market.Replace("-", ""), this.ProdType, this.SourceExchange);
        if (inst == null) return RestApiResult<OrderStatusReport>.Failure(new RestApiException("Instrument not found."));

        // 상세 조회 데이터를 바탕으로 리포트 생성
        var report = new OrderStatusReport(
            clientOrderId: 0,
            exchangeOrderId: data.Uuid,
            executionId: null,
            instrumentId: inst.InstrumentId,
            side: data.Side == "bid" ? Side.Buy : Side.Sell,
            status: MapStateToStatus(data.State),
            price: Price.FromDecimal(decimal.Parse(data.Price)),
            quantity: Quantity.FromDecimal(decimal.Parse(data.Volume)),
            leavesQuantity: Quantity.FromDecimal(decimal.Parse(data.RemainingVolume)),
            timestamp: DateTimeOffset.Parse(data.CreatedAt).ToUnixTimeMilliseconds()
        );

        return RestApiResult<OrderStatusReport>.Success(report);
    }

    public async Task CancelAllOrdersAsync(string symbol, CancellationToken cancellationToken = default)
    {
        // 빗썸은 일괄 취소 API가 없으므로 개별 취소 루프를 돌거나 에러 처리
        _logger.LogWarningWithCaller("Bulk cancel not supported natively by Bithumb API.");
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<OrderModificationResult>> SendBulkCancelOrdersAsync(BulkCancelOrdersRequest request, CancellationToken cancellationToken = default)
    {
        var results = new List<OrderModificationResult>();
        foreach (var id in request.ExchangeOrderIds)
        {
            results.Add(await SendCancelOrderAsync(new CancelOrderRequest(id, request.InstrumentId), cancellationToken));
        }
        return results;
    }

    // --- Helper Methods ---

    private OrderStatus MapStateToStatus(string state) => state switch
    {
        "wait" => OrderStatus.New,
        "trade" => OrderStatus.PartiallyFilled,
        "done" => OrderStatus.Filled,
        "cancel" => OrderStatus.Cancelled,
        _ => OrderStatus.Pending
    };

    // V2 취소 응답 모델
    private record BithumbCancelResponse(
        [property: JsonPropertyName("order_id")] string OrderId,
        [property: JsonPropertyName("created_at")] string CreatedAt
    );

    // V1 조회 응답 모델
    private record BithumbOrderLookupResponse(
        [property: JsonPropertyName("uuid")] string Uuid,
        [property: JsonPropertyName("side")] string Side,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("market")] string Market,
        [property: JsonPropertyName("price")] string Price,
        [property: JsonPropertyName("volume")] string Volume,
        [property: JsonPropertyName("remaining_volume")] string RemainingVolume,
        [property: JsonPropertyName("created_at")] string CreatedAt
    );

    private string MapOrderType(OrderType type) => type switch
    {
        OrderType.Limit => "limit",
        OrderType.Market => "market",
        _ => "limit"
    };

    private OrderStatusReport? MapResponseToReport(BithumbOrderResponse response, int instrumentId, Side? requestedSide)
    {
        return new OrderStatusReport(
            clientOrderId: 0,
            exchangeOrderId: response.OrderId,
            executionId: null,
            instrumentId: instrumentId,
            side: requestedSide ?? (response.Side == "bid" ? Side.Buy : Side.Sell),
            status: response.State == "done" ? OrderStatus.Filled : OrderStatus.New,
            price: Price.FromDecimal(decimal.TryParse(response.Price, out var p) ? p : 0),
            quantity: Quantity.FromDecimal(decimal.TryParse(response.Volume, out var v) ? v : 0),
            leavesQuantity: Quantity.FromDecimal(0), // 정확한 잔량은 상세 조회 필요
            timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
    }

    // 빗썸 전용 내부 모델
    private record BithumbOrderResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("order_id")] string OrderId,
        [property: System.Text.Json.Serialization.JsonPropertyName("market")] string Market,
        [property: System.Text.Json.Serialization.JsonPropertyName("side")] string Side,
        [property: System.Text.Json.Serialization.JsonPropertyName("state")] string State,
        [property: System.Text.Json.Serialization.JsonPropertyName("price")] string Price,
        [property: System.Text.Json.Serialization.JsonPropertyName("volume")] string Volume
    );
}
