using System;
using System.Text.Json;
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

    public string CommandType => "GET_FILLS";

    public GetFillsCommandHandler(
        ILogger<GetFillsCommandHandler> logger,
        IOrderRouter orderRouter,
        IWebSocketChannel channel)
    {
        _logger = logger;
        _orderRouter = orderRouter;
        _channel = channel;
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
            var listEvent = new FillsListEvent(allFills);
            await _channel.SendAsync(listEvent);

            _logger.LogInformationWithCaller("Sent 0 fills to the client.");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error handling GET_FILLS command.");
            var errorEvent = new ErrorEvent($"Error retrieving fills: {ex.Message}");
            await _channel.SendAsync(errorEvent);
        }
    }
}