using System;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

/// <summary>
/// Handles the 'GET_INSTANCE_STATUSES' command from a WebSocket client.
/// This handler retrieves all active quoting instances from the manager
/// and sends an 'INSTANCE_STATUS' event for each one back to the client.
/// </summary>
public class GetInstanceStatusesCommandHandler : IWebSocketCommandHandler
{
    private readonly ILogger<GetInstanceStatusesCommandHandler> _logger;
    private readonly IQuotingInstanceManager _manager;
    private readonly IWebSocketChannel _channel;
    private readonly string _omsIdentifier;

    public string CommandType => "GET_INSTANCE_STATUSES";

    public GetInstanceStatusesCommandHandler(
        ILogger<GetInstanceStatusesCommandHandler> logger,
        IQuotingInstanceManager manager,
        IWebSocketChannel channel,
        IConfiguration config)
    {
        _logger = logger;
        _manager = manager;
        _channel = channel;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
    }

    public async Task HandleAsync(JsonElement messageElement)
    {
        _logger.LogInformationWithCaller($"Handling {CommandType} command.");
        string? correlationId = messageElement.TryGetProperty("correlationId", out var idEl) ? idEl.GetString() : null;

        try
        {
            // 1. Retrieve all running instances from the instance manager.
            var allInstances = _manager.GetAllInstances();

            int count = 0;
            foreach (var instance in allInstances)
            {
                // 2. For each instance, create a status payload.
                var payload = new InstanceStatusPayload
                {
                    OmsIdentifier = _omsIdentifier,
                    InstrumentId = instance.InstrumentId,
                    IsActive = instance.IsActive,
                    Parameters = instance.CurrentParameters
                };

                // 3. Wrap the payload in an InstanceStatusEvent.
                var statusEvent = new InstanceStatusEvent(payload);

                // 4. Send the event back to the requesting client.
                await _channel.SendAsync(statusEvent);
                count++;
            }

            _logger.LogInformationWithCaller($"Sent status for {count} active instances.");
        }
        catch (Exception ex)
        {
            var msg = $"Error handling {CommandType} command.";
            _logger.LogErrorWithCaller(ex, msg);
            // Send a generic error event back to the client.
            var errorPayload = new ErrorPayload(_omsIdentifier, msg);
            var errorEvent = new ErrorEvent(errorPayload);
            await _channel.SendAsync(errorEvent);
        }
    }
}