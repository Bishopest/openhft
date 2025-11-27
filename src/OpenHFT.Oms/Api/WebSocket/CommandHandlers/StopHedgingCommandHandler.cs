using System;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Hedging;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class StopHedgingCommandHandler : IWebSocketCommandHandler
{
    private readonly ILogger<RetireInstanceCommandHandler> _logger;
    private readonly IHedgerManager _hedgingManager;
    private readonly IWebSocketChannel _channel;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _omsIdentifier;

    public string CommandType => "STOP_HEDGING";

    public StopHedgingCommandHandler(ILogger<RetireInstanceCommandHandler> logger, IHedgerManager manager, IWebSocketChannel channel, IConfiguration config)
    {
        _logger = logger;
        _hedgingManager = manager;
        _channel = channel;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
    }

    public async Task HandleAsync(JsonElement messageElement)
    {
        string? correlationId = messageElement.TryGetProperty("correlationId", out var idEl) ? idEl.GetString() : null;

        try
        {
            if (!messageElement.TryGetProperty("payload", out var payloadElement))
            {
                _logger.LogWarningWithCaller("Received STOP_HEDGING command without 'payload'.");
                return;
            }

            var id = JsonSerializer.Deserialize<int>(payloadElement.GetRawText(), _jsonOptions);

            _logger.LogInformationWithCaller("Handling STOP_HEDGING command.");
            var success = _hedgingManager.StopHedging(id);
            if (success)
            {
                var ackPayload = new AckPayload(_omsIdentifier, correlationId ?? string.Empty, false, "Failed to stop hedging.");
                var ackEvent = new AcknowledgmentEvent(ackPayload);
                await _channel.SendAsync(ackEvent);
            }
        }
        catch (Exception ex)
        {
            var msg = "Error handling STOP_HEDGING command.";
            _logger.LogErrorWithCaller(ex, msg);
            var errorPayload = new ErrorPayload(_omsIdentifier, msg);
            var errorEvent = new ErrorEvent(errorPayload);
            await _channel.SendAsync(errorEvent);
        }
    }
}
