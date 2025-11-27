using System;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Hedging;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class UpdateHedgingParametersCommandHandler : IWebSocketCommandHandler
{
    private readonly ILogger<UpdateHedgingParametersCommandHandler> _logger;
    private readonly IWebSocketChannel _channel;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _omsIdentifier;

    private readonly IHedgerManager _hedgingManager;
    public string CommandType => "UPDATE_HEDGING_PARAMETERS";
    public UpdateHedgingParametersCommandHandler(ILogger<UpdateHedgingParametersCommandHandler> logger, IHedgerManager hedgingManager, IWebSocketChannel channel, JsonSerializerOptions jsonOptions, IConfiguration config)
    {
        _logger = logger;
        _hedgingManager = hedgingManager;
        _channel = channel;
        _jsonOptions = jsonOptions;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
    }

    public async Task HandleAsync(JsonElement messageElement)
    {
        string? correlationId = messageElement.TryGetProperty("correlationId", out var idEl) ? idEl.GetString() : null;

        try
        {
            if (!messageElement.TryGetProperty("payload", out var payloadElement))
            {
                _logger.LogWarningWithCaller("Received UPDATE_HEDGING_PARAMETERS command without 'payload'.");
                return;
            }

            var parameters = JsonSerializer.Deserialize<HedgingParameters>(payloadElement.GetRawText(), _jsonOptions);

            _logger.LogInformationWithCaller("Handling UPDATE_HEDGING_PARAMETERS command.");
            var success = _hedgingManager.UpdateHedgingParameters(parameters);
            if (success)
            {
                var ackPayload = new AckPayload(_omsIdentifier, correlationId ?? string.Empty, false, "Failed to update hedging parameters.");
                var ackEvent = new AcknowledgmentEvent(ackPayload);
                await _channel.SendAsync(ackEvent);
            }
        }
        catch (Exception ex)
        {
            var msg = "Error handling UPDATE_HEDGING_PARAMETERS command.";
            _logger.LogErrorWithCaller(ex, msg);
            var errorPayload = new ErrorPayload(_omsIdentifier, msg);
            var errorEvent = new ErrorEvent(errorPayload);
            await _channel.SendAsync(errorEvent);
        }
    }
}
