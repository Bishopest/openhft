using System;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;

namespace OpenHFT.Oms.Api.WebSocket.CommandHandlers;

public class ManualOrderCommandHandler : IWebSocketCommandHandler
{
    public string CommandType => "MANUAL_ORDER";

    private readonly ILogger<ManualOrderCommandHandler> _logger;
    private readonly IOrderFactory _orderFactory;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly IWebSocketChannel _channel;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _omsIdentifier;

    public ManualOrderCommandHandler(
        ILogger<ManualOrderCommandHandler> logger,
        IOrderFactory orderFactory,
        IInstrumentRepository instrumentRepository,
        IWebSocketChannel channel,
        JsonSerializerOptions jsonOptions,
        IConfiguration config)
    {
        _logger = logger;
        _orderFactory = orderFactory;
        _instrumentRepository = instrumentRepository;
        _channel = channel;
        _jsonOptions = jsonOptions;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
    }

    public async Task HandleAsync(JsonElement messageElement)
    {
        string? correlationId = messageElement.TryGetProperty("correlationId", out var idEl) ? idEl.GetString() : null;

        try
        {
            if (!messageElement.TryGetProperty("payload", out var payloadElement))
            {
                _logger.LogWarningWithCaller("Received MANUAL_ORDER command without 'payload'.");
                return;
            }

            var payload = JsonSerializer.Deserialize<ManualOrderPayload>(payloadElement.GetRawText(), _jsonOptions);

            _logger.LogInformationWithCaller("Handling MANUAL_ORDER command.");
            var instrument = _instrumentRepository.GetById(payload.InstrumentId);
            if (instrument == null)
            {
                _logger.LogWarningWithCaller($"Cannot place manual order: Instrument with ID {payload.InstrumentId} not found.");
                return;
            }

            _logger.LogInformationWithCaller($"Received manual order request for {instrument.Symbol}: Size {payload.Size} @ {payload.Price} in Book {payload.BookName}");

            var side = payload.IsBuy ? Side.Buy : Side.Sell;
            var quantity = Quantity.FromDecimal(Math.Abs(payload.Size.ToDecimal()));

            var orderBuilder = new OrderBuilder(_orderFactory, payload.InstrumentId, side, payload.BookName, OrderSource.Manual);

            var manualOrder = orderBuilder
                .WithPrice(payload.Price)
                .WithQuantity(quantity)
                .WithOrderType(OrderType.Limit)
                .WithPostOnly(payload.PostOnly)
                .Build();

            await manualOrder.SubmitAsync();

            _logger.LogInformationWithCaller($"Successfully submitted manual order with ClientOrderId {manualOrder.ClientOrderId}");

            var ackPayload = new AckPayload(_omsIdentifier, correlationId ?? string.Empty, true);
            var ackEvent = new AcknowledgmentEvent(ackPayload);
            await _channel.SendAsync(ackEvent);
        }
        catch (Exception ex)
        {
            var msg = "Error handling MANUAL_ORDER command.";
            _logger.LogErrorWithCaller(ex, msg);
            var errorPayload = new ErrorPayload(_omsIdentifier, msg);
            var errorEvent = new ErrorEvent(errorPayload);
            await _channel.SendAsync(errorEvent);
        }
    }
}
