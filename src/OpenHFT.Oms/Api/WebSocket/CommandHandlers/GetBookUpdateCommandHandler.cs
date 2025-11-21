using System;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Books;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Utils;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class GetBookUpdateCommandHandler : IWebSocketCommandHandler
{
    private readonly ILogger<GetBookUpdateCommandHandler> _logger;
    private readonly IBookManager _manager;
    private readonly IWebSocketChannel _channel;
    private readonly string _omsIdentifier;

    public string CommandType => "GET_BOOK_UPDATE";

    public GetBookUpdateCommandHandler(
        ILogger<GetBookUpdateCommandHandler> logger,
        IBookManager manager,
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
            var allBookElements = _manager.GetAllBookElements();
            var allBookInfos = _manager.GetAllBookInfos();

            var payload = new BookUpdatePayload(_omsIdentifier, allBookInfos, allBookElements);
            var statusEvent = new BookUpdateEvent(payload);
            await _channel.SendAsync(statusEvent);

            _logger.LogInformationWithCaller($"Sent status for {allBookElements.Count} book elements & {allBookInfos.Count} book info.");
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
