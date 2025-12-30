using System;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Utils;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class ManualOrderCancelCommandHandler : IWebSocketCommandHandler
{
    public string CommandType => "MANUAL_ORDER_CANCEL";

    private readonly ILogger<ManualOrderCancelCommandHandler> _logger;
    private readonly IOrderRouter _orderRouter;
    private readonly IWebSocketChannel _channel;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _omsIdentifier;

    public ManualOrderCancelCommandHandler(
        ILogger<ManualOrderCancelCommandHandler> logger,
        IOrderRouter orderRouter,
        IWebSocketChannel channel,
        JsonSerializerOptions jsonOptions,
        IConfiguration config)
    {
        _logger = logger;
        _orderRouter = orderRouter;
        _channel = channel;
        _jsonOptions = jsonOptions;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
    }

    public async Task HandleAsync(JsonElement messageElement)
    {
        string? correlationId = messageElement.TryGetProperty("correlationId", out var idEl) ? idEl.GetString() : null;

        if (!messageElement.TryGetProperty("payload", out var payloadElement))
        {
            _logger.LogWarningWithCaller("Received MANUAL_ORDER_CANCEL command without 'payload'.");
            return;
        }

        var payload = JsonSerializer.Deserialize<ManualOrderCancelPayload>(payloadElement.GetRawText(), _jsonOptions);
        var orderIdString = payload.OrderId;
        if (string.IsNullOrEmpty(orderIdString))
        {
            _logger.LogWarningWithCaller("Received manual order cancel request with an empty OrderId.");
            return;
        }

        try
        {
            _logger.LogInformationWithCaller($"Handling MANUAL_ORDER_CANCEL({orderIdString}) command.");

            // --- Find the order to cancel ---
            IOrder? orderToCancel = null;

            orderToCancel = _orderRouter.GetActiveOrders().FirstOrDefault(o =>
                !string.IsNullOrEmpty(o.ExchangeOrderId) && o.ExchangeOrderId.Equals(orderIdString, StringComparison.OrdinalIgnoreCase));

            if (orderToCancel == null)
            {
                _logger.LogWarningWithCaller($"Could not find an active order with ID '{orderIdString}' to cancel.");
                // Optionally, send a failure acknowledgment back to the GUI.
                var ackPayload = new AckPayload(_omsIdentifier, correlationId ?? string.Empty, false, "Order not found.");
                var ackEvent = new AcknowledgmentEvent(ackPayload);
                await _channel.SendAsync(ackEvent);
                return;
            }

            await orderToCancel.CancelAsync();
            _logger.LogInformationWithCaller($"Successfully sent cancel request for Order ID {orderIdString} (ClientOrderId: {orderToCancel.ClientOrderId}).");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"An error occurred while attempting to cancel manual order {orderIdString}.");
        }
    }
}