using System.Text.Json;
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


    public RetireInstanceCommandHandler(ILogger<RetireInstanceCommandHandler> logger, IQuotingInstanceManager manager, IWebSocketChannel channel)
    {
        _logger = logger;
        _manager = manager;
        _channel = channel;
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
            var success = _manager.RetireInstance(id);
            var message = success ? "Strategy retired successfully." : "Failed to retire strategy.";

            var ackEvent = new AcknowledgmentEvent(correlationId ?? string.Empty, success, message);
            await _channel.SendAsync(ackEvent);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error handling RETIRE_INSTANCE command.");
            var errorEvent = new ErrorEvent(ex.Message);
            await _channel.SendAsync(errorEvent);
        }
    }
}