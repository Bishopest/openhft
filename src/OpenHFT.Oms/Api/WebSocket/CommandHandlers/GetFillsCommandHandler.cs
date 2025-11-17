using System;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class GetFillsCommandHandler : IWebSocketCommandHandler
{
    private readonly ILogger<GetFillsCommandHandler> _logger;
    private readonly IFillRepository _fillRepository;
    private readonly IOrderRouter _orderRouter;
    private readonly IWebSocketChannel _channel;
    private readonly string _omsIdentifier;

    public string CommandType => "GET_FILLS";

    public GetFillsCommandHandler(
        ILogger<GetFillsCommandHandler> logger,
        IFillRepository fillRepository,
        IOrderRouter orderRouter,
        IWebSocketChannel channel,
        IConfiguration config)
    {
        _logger = logger;
        _fillRepository = fillRepository;
        _orderRouter = orderRouter;
        _channel = channel;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
    }

    public async Task HandleAsync(JsonElement messageElement)
    {
        _logger.LogInformationWithCaller("Handling GET_FILLS command.");

        try
        {
            var targetDate = DateTime.UtcNow.Date;

            var allFills = await _fillRepository.GetFillsByDateAsync(targetDate);

            // 3. FillsListEvent 메시지를 생성하여 전송합니다.
            var payload = new FillsPayload(_omsIdentifier, allFills);
            var listEvent = new FillsListEvent(payload);
            await _channel.SendAsync(listEvent);

            _logger.LogInformationWithCaller("Sent 0 fills to the client.");
        }
        catch (Exception ex)
        {
            var msg = "Error handling GET_FILLS command.";
            _logger.LogErrorWithCaller(ex, msg);
            var errorPayload = new ErrorPayload(_omsIdentifier, msg);
            var errorEvent = new ErrorEvent(errorPayload);
            await _channel.SendAsync(errorEvent);
        }
    }
}