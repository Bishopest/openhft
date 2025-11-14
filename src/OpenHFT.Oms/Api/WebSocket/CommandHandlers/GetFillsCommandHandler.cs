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
    private readonly IOrderRouter _orderRouter;
    private readonly IWebSocketChannel _channel;
    private readonly string _omsIdentifier;

    public string CommandType => "GET_FILLS";

    public GetFillsCommandHandler(
        ILogger<GetFillsCommandHandler> logger,
        IOrderRouter orderRouter,
        IWebSocketChannel channel,
        IConfiguration config)
    {
        _logger = logger;
        _orderRouter = orderRouter;
        _channel = channel;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
    }

    public async Task HandleAsync(JsonElement messageElement)
    {
        _logger.LogInformationWithCaller("Handling GET_FILLS command.");

        try
        {
            // 1. 모든 활성 주문 목록을 가져옵니다.
            var activeOrders = _orderRouter.GetActiveOrders();

            // 2. 각 주문의 체결 내역(Fills)을 하나의 리스트로 합칩니다.
            var allFills = Enumerable.Empty<Fill>();

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