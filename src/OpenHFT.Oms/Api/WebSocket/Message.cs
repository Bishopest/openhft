using System.Text.Json.Serialization;
using OpenHFT.Core.Models;
using OpenHFT.Quoting;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Oms.Api.WebSocket;

// --- Base Message Structure ---
public abstract record WebSocketMessage(
    [property: JsonPropertyName("type")] string Type
);

// --- Client -> Server (Commands) ---
public record RetireInstanceCommand(
    [property: JsonPropertyName("payload")] int InstrumentId
) : WebSocketMessage("RETIRE_INSTANCE");

public record UpdateParametersCommand(
    [property: JsonPropertyName("payload")] QuotingParameters Parameters
) : WebSocketMessage("UPDATE_PARAMETERS");

public record GetInstanceStatusesCommand() : WebSocketMessage("GET_INSTANCE_STATUSES");
public record GetActiveOrdersCommand() : WebSocketMessage("GET_ACTIVE_ORDERS");
public record GetFillsCommand() : WebSocketMessage("GET_FILLS");

// --- Server -> Client (Events / Responses) ---
public record AcknowledgmentEvent(
        [property: JsonPropertyName("payload")] AckPayload Payload
    ) : WebSocketMessage("ACK");

public record AckPayload(
    [property: JsonPropertyName("omsIdentifier")] string OmsIdentifier,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message = null
);

public record ActiveOrdersListEvent(
    [property: JsonPropertyName("payload")] ActiveOrdersPayload Payload
) : WebSocketMessage("ACTIVE_ORDERS_LIST");

public record ActiveOrdersPayload(
    [property: JsonPropertyName("omsIdentifier")] string OmsIdentifier,
    [property: JsonPropertyName("reports")] IEnumerable<OrderStatusReport> Reports
);

public record FillsListEvent(
    [property: JsonPropertyName("payload")] FillsPayload Payload
) : WebSocketMessage("FILLS_LIST");

public record FillsPayload(
    [property: JsonPropertyName("omsIdentifier")] string OmsIdentifier,
    [property: JsonPropertyName("fills")] IEnumerable<Fill> Fills
);

public record InstanceStatusEvent(
    [property: JsonPropertyName("payload")] InstanceStatusPayload Payload
) : WebSocketMessage("INSTANCE_STATUS");

// The existing InstanceStatusPayload needs the identifier.
public class InstanceStatusPayload
{
    [JsonPropertyName("omsIdentifier")]
    public string OmsIdentifier { get; set; } = string.Empty;

    [JsonPropertyName("instrumentId")]
    public int InstrumentId { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("parameters")]
    public QuotingParameters Parameters { get; set; }
}

public record QuotePairUpdateEvent(
    [property: JsonPropertyName("payload")] QuotePairUpdatePayload Payload
) : WebSocketMessage("QUOTEPAIR_UPDATE");

public record QuotePairUpdatePayload(
    [property: JsonPropertyName("omsIdentifier")] string OmsIdentifier,
    [property: JsonPropertyName("quotePair")] QuotePair QuotePair
);
public record ErrorEvent(
        [property: JsonPropertyName("payload")] ErrorPayload Payload
    ) : WebSocketMessage("ERROR");

public record ErrorPayload(
    [property: JsonPropertyName("omsIdentifier")] string OmsIdentifier,
    [property: JsonPropertyName("message")] string Message
);