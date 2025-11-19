using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;
namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class RetireInstanceCommandHandler : IWebSocketCommandHandler
{
    private readonly ILogger<RetireInstanceCommandHandler> _logger;
    private readonly IQuotingInstanceManager _manager;
    private readonly IWebSocketChannel _channel;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _omsIdentifier;

    public RetireInstanceCommandHandler(ILogger<RetireInstanceCommandHandler> logger, IQuotingInstanceManager manager, IWebSocketChannel channel, IConfiguration config)
    {
        _logger = logger;
        _manager = manager;
        _channel = channel;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
    }

    public string CommandType => "RETIRE_INSTANCE";
    public async Task HandleAsync(JsonElement messageElement)
    {
        string? correlationId = messageElement.TryGetProperty("correlationId", out var idEl) ? idEl.GetString() : null;

        try
        {
            if (!messageElement.TryGetProperty("payload", out var payloadElement))
            {
                _logger.LogWarningWithCaller("Received RETIRE_INSTANCE command without 'payload'.");
                return;
            }

            var id = JsonSerializer.Deserialize<int>(payloadElement.GetRawText(), _jsonOptions);

            _logger.LogInformationWithCaller("Handling RETIRE_INSTANCE command.");
            var resultInstance = _manager.RetireInstance(id);
            if (resultInstance == null)
            {
                var ackPayload = new AckPayload(_omsIdentifier, correlationId ?? string.Empty, false, "Failed to retire quoting instance.");
                var ackEvent = new AcknowledgmentEvent(ackPayload);
                await _channel.SendAsync(ackEvent);
            }
        }
        catch (Exception ex)
        {
            var msg = "Error handling RETIRE_INSTANCE command.";
            _logger.LogErrorWithCaller(ex, msg);
            var errorPayload = new ErrorPayload(_omsIdentifier, msg);
            var errorEvent = new ErrorEvent(errorPayload);
            await _channel.SendAsync(errorEvent);
        }
    }
}