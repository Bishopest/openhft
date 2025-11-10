using System;
using System.Text.Json;
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

    public string CommandType => "GET_INSTANCE_STATUSES";

    public GetInstanceStatusesCommandHandler(
        ILogger<GetInstanceStatusesCommandHandler> logger,
        IQuotingInstanceManager manager,
        IWebSocketChannel channel)
    {
        _logger = logger;
        _manager = manager;
        _channel = channel;
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
            _logger.LogErrorWithCaller(ex, $"Error handling {CommandType} command.");
            // Send a generic error event back to the client.
            var errorEvent = new ErrorEvent($"An unexpected error occurred while fetching instance statuses: {ex.Message}");
            await _channel.SendAsync(errorEvent);
        }
    }
}