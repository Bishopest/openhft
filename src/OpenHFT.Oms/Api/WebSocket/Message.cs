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
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message = null
) : WebSocketMessage("ACK");

/// <summary>
/// Response to GET_ACTIVE_ORDERS, containing a list of the latest status reports for all active orders.
/// </summary>
public record ActiveOrdersListEvent(
    [property: JsonPropertyName("payload")] IEnumerable<OrderStatusReport> Reports
) : WebSocketMessage("ACTIVE_ORDERS_LIST");

/// <summary>
/// Response to GET_FILLS, containing a list of all known fills for active orders.
/// </summary>
public record FillsListEvent(
    [property: JsonPropertyName("payload")] IEnumerable<Fill> Fills
) : WebSocketMessage("FILLS_LIST");

public record InstanceStatusEvent(
    [property: JsonPropertyName("payload")] InstanceStatusPayload Payload
) : WebSocketMessage("INSTANCE_STATUS");

public class InstanceStatusPayload
{
    public int InstrumentId { get; set; }
    public bool IsActive { get; set; }
    public QuotingParameters Parameters { get; set; }
    // Add other status info like PnL, number of orders, etc.
}

public record QuotePairUpdateEvent(
    [property: JsonPropertyName("payload")] QuotePair QuotePair
) : WebSocketMessage("QUOTEPAIR_UPDATE");

public record ErrorEvent(
    [property: JsonPropertyName("message")] string Message
) : WebSocketMessage("ERROR");