using System;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Utils;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class GetActiveOrdersCommandHandler : IWebSocketCommandHandler
{
    private readonly ILogger<GetActiveOrdersCommandHandler> _logger;
    private readonly IOrderRouter _orderRouter;
    private readonly IWebSocketChannel _channel;
    private readonly string _omsIdentifier;

    public string CommandType => "GET_ACTIVE_ORDERS";

    public GetActiveOrdersCommandHandler(
        ILogger<GetActiveOrdersCommandHandler> logger,
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
            var payload = new ActiveOrdersPayload(_omsIdentifier, latestReports);
            var listEvent = new ActiveOrdersListEvent(payload);
            await _channel.SendAsync(listEvent);

            _logger.LogInformationWithCaller($"Sent {latestReports.Count} active order statuses to the client.");
        }
        catch (Exception ex)
        {
            var msg = "Error handling GET_ACTIVE_ORDERS command.";
            _logger.LogErrorWithCaller(ex, msg);
            var errorpayload = new ErrorPayload(_omsIdentifier, msg);
            var errorEvent = new ErrorEvent(errorpayload);
            await _channel.SendAsync(errorEvent);
        }
    }
}