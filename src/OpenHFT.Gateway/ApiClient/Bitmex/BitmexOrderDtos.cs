using System;
using System.Text.Json.Serialization;

namespace OpenHFT.Gateway.ApiClient.Bitmex;
// POST /api/v1/order 요청 본문
public class BitmexNewOrderPayload
{
    [JsonPropertyName("symbol")] public string Symbol { get; set; }
    [JsonPropertyName("side")] public string Side { get; set; }
    [JsonPropertyName("orderQty")] public decimal OrderQty { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("ordType")] public string OrdType { get; set; }
    [JsonPropertyName("clOrdID")] public string ClOrdId { get; set; }
    [JsonPropertyName("execInst")] public string? ExecInst { get; set; }
}

// PUT /api/v1/order 요청 본문
public class BitmexAmendOrderPayload
{
    // 주문을 식별하기 위해 orderID 또는 origClOrdID 중 하나가 필요
    [JsonPropertyName("orderID")] public string? OrderId { get; set; }
    [JsonPropertyName("origClOrdID")] public string? OrigClOrdId { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("orderQty")] public decimal? OrderQty { get; set; }
}

// DELETE /api/v1/order 요청 본문
public class BitmexCancelOrderPayload
{
    [JsonPropertyName("clOrdID")] public string? ClOrdId { get; set; }
}

// BitMEX 주문 응답 (성공 시, 신규/수정/취소 모두 유사한 구조 반환)
public class BitmexOrderResponse
{
    [JsonPropertyName("orderID")] public string OrderId { get; set; }
    [JsonPropertyName("clOrdID")] public string? ClOrdId { get; set; }
    [JsonPropertyName("ordStatus")] public string OrdStatus { get; set; }
    [JsonPropertyName("symbol")] public string Symbol { get; set; }
    [JsonPropertyName("side")] public string Side { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("orderQty")] public decimal? OrderQty { get; set; }
    [JsonPropertyName("leavesQty")] public decimal? LeavesQty { get; set; }
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
}