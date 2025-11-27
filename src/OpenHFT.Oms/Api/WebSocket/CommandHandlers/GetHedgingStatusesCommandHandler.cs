using System;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Hedging;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class GetHedgingStatusesCommandHandler : IWebSocketCommandHandler
{
    private readonly ILogger<GetHedgingStatusesCommandHandler> _logger;
    private readonly IHedgerManager _manager;
    private readonly IWebSocketChannel _channel;
    private readonly string _omsIdentifier;

    public string CommandType => "GET_HEDGING_STATUSES";

    public GetHedgingStatusesCommandHandler(
        ILogger<GetHedgingStatusesCommandHandler> logger,
        IHedgerManager manager,
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
            var allHedgers = _manager.GetAllHedgers();

            int count = 0;
            foreach (var hedger in allHedgers)
            {
                var payload = new HedgingStatusPayload
                {
                    OmsIdentifier = _omsIdentifier,
                    IsActive = hedger.IsActive,
                    Parameters = hedger.HedgeParameters
                };

                var statusEvent = new HedgingStatusEvent(payload);

                await _channel.SendAsync(statusEvent);
                count++;
            }

            _logger.LogInformationWithCaller($"Sent hedge status for {count} active hedgers.");
        }
        catch (Exception ex)
        {
            var msg = $"Error handling {CommandType} command.";
            _logger.LogErrorWithCaller(ex, msg);
            var errorPayload = new ErrorPayload(_omsIdentifier, msg);
            var errorEvent = new ErrorEvent(errorPayload);
            await _channel.SendAsync(errorEvent);
        }
    }


}
