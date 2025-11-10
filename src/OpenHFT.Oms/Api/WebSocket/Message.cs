using System.Text.Json.Serialization;
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
) : WebSocketMessage("RETIRE_STRATEGY");

public record UpdateParametersCommand(
    [property: JsonPropertyName("payload")] QuotingParameters Parameters
) : WebSocketMessage("UPDATE_PARAMETERS");

// --- Server -> Client (Events / Responses) ---
public record AcknowledgmentEvent(
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message = null
) : WebSocketMessage("ACK");

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