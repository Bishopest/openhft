using System;
using System.Text.Json;
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
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string CommandType => "UPDATE_PARAMETERS";

    public UpdateParametersCommandHandler(ILogger<UpdateParametersCommandHandler> logger, IQuotingInstanceManager manager, IWebSocketChannel channel)
    {
        _logger = logger;
        _manager = manager;
        _channel = channel;
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
            var success = _manager.DeployInstance(parameters);
            var message = success ? "Strategy deployed successfully." : "Failed to deploy strategy.";

            var ackEvent = new AcknowledgmentEvent(correlationId ?? string.Empty, success, message);
            await _channel.SendAsync(ackEvent);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error handling UPDATE_PARAMETERS command.");
            var errorEvent = new ErrorEvent(ex.Message);
            await _channel.SendAsync(errorEvent);
        }
    }
}
