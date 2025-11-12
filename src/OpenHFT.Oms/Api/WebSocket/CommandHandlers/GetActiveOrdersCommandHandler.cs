using System;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Utils;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class GetActiveOrdersCommandHandler : IWebSocketCommandHandler
{
    private readonly ILogger<GetActiveOrdersCommandHandler> _logger;
    private readonly IOrderRouter _orderRouter;
    private readonly IWebSocketChannel _channel;

    public string CommandType => "GET_ACTIVE_ORDERS";

    public GetActiveOrdersCommandHandler(
        ILogger<GetActiveOrdersCommandHandler> logger,
        IOrderRouter orderRouter,
        IWebSocketChannel channel)
    {
        _logger = logger;
        _orderRouter = orderRouter;
        _channel = channel;
    }

    public async Task HandleAsync(JsonElement messageElement)
    {
        _logger.LogInformationWithCaller("Handling GET_ACTIVE_ORDERS command.");

        try
        {
            // 1. OrderRouter로부터 모든 활성 주문 목록을 가져옵니다.
            var activeOrders = _orderRouter.GetActiveOrders();

            // 2. --- CHANGED ---
            // 각 IOrder 객체의 LatestReport 속성을 수집합니다.
            // LatestReport가 null이 아닌 주문들만 필터링합니다.
            var latestReports = activeOrders
                .Where(order => order.LatestReport.HasValue)
                .Select(order => order.LatestReport!.Value)
                .ToList();

            // 3. ActiveOrdersListEvent 메시지를 생성하여 전송합니다.
            var listEvent = new ActiveOrdersListEvent(latestReports);
            await _channel.SendAsync(listEvent);

            _logger.LogInformationWithCaller($"Sent {latestReports.Count} active order statuses to the client.");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error handling GET_ACTIVE_ORDERS command.");
            var errorEvent = new ErrorEvent($"Error retrieving active orders: {ex.Message}");
            await _channel.SendAsync(errorEvent);
        }
    }
}