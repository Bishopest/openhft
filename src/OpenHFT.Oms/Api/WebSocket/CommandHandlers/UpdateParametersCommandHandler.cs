using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class UpdateParametersCommandHandler : IWebSocketCommandHandler
{
    private readonly ILogger<UpdateParametersCommandHandler> _logger;
    private readonly IQuotingInstanceManager _manager;
    private readonly IWebSocketChannel _channel;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _omsIdentifier;

    public string CommandType => "UPDATE_PARAMETERS";

    public UpdateParametersCommandHandler(ILogger<UpdateParametersCommandHandler> logger, IQuotingInstanceManager manager, IWebSocketChannel channel, JsonSerializerOptions jsonOptions, IConfiguration config)
    {
        _logger = logger;
        _manager = manager;
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
                _logger.LogWarningWithCaller("Received UPDATE_PARAMETERS command without 'payload'.");
                return;
            }

            var parameters = JsonSerializer.Deserialize<QuotingParameters>(payloadElement.GetRawText(), _jsonOptions);

            _logger.LogInformationWithCaller("Handling UPDATE_PARAMETERS command.");
            var resultInstance = _manager.UpdateInstanceParameters(parameters);
            if (resultInstance != null)
            {
                var payload = new InstanceStatusPayload
                {
                    OmsIdentifier = _omsIdentifier,
                    InstrumentId = resultInstance.InstrumentId,
                    IsActive = resultInstance.IsActive,
                    Parameters = resultInstance.CurrentParameters
                };
                var statusEvent = new InstanceStatusEvent(payload);
                await _channel.SendAsync(statusEvent);
            }
            else
            {
                var ackPayload = new AckPayload(_omsIdentifier, correlationId ?? string.Empty, false, "Failed to retire quoting instance.");
                var ackEvent = new AcknowledgmentEvent(ackPayload);
                await _channel.SendAsync(ackEvent);
            }
        }
        catch (Exception ex)
        {
            var msg = "Error handling UPDATE_PARAMETERS command.";
            _logger.LogErrorWithCaller(ex, msg);
            var errorPayload = new ErrorPayload(_omsIdentifier, msg);
            var errorEvent = new ErrorEvent(errorPayload);
            await _channel.SendAsync(errorEvent);
        }
    }
}