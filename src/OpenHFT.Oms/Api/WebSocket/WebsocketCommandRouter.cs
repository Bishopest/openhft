using System;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;

namespace OpenHFT.Oms.Api.WebSocket;

public interface IWebSocketCommandRouter
{
    Task RouteAsync(string messageJson);
}

public class WebSocketCommandRouter : IWebSocketCommandRouter
{
    private readonly ILogger<WebSocketCommandRouter> _logger;
    private readonly IReadOnlyDictionary<string, IWebSocketCommandHandler> _handlers;
    private readonly IWebSocketChannel _channel;
    private readonly string _omsIdentifier;

    public WebSocketCommandRouter(
        ILogger<WebSocketCommandRouter> logger,
        IEnumerable<IWebSocketCommandHandler> handlers,
        IWebSocketChannel channel,
        IConfiguration config)
    {
        _logger = logger;
        _handlers = handlers.ToDictionary(h => h.CommandType, h => h);
        _channel = channel;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
        _logger.LogInformation("WebSocketCommandRouter initialized with {Count} handlers: {Handlers}",
            _handlers.Count, string.Join(", ", _handlers.Keys));
    }

    public async Task RouteAsync(string messageJson)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(messageJson);
            if (!jsonDoc.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.GetString() is not { } messageType)
            {
                _logger.LogWarningWithCaller("Received WebSocket message without a 'type' property.");
                return;
            }

            if (_handlers.TryGetValue(messageType, out var handler))
            {
                await handler.HandleAsync(jsonDoc.RootElement);
            }
            else
            {
                var errorMsg = $"No handler found for WebSocket message type: {messageType}";
                _logger.LogWarningWithCaller(errorMsg);
                var errorPayload = new ErrorPayload(_omsIdentifier, errorMsg);
                var errorEvent = new ErrorEvent(errorPayload);
                await _channel.SendAsync(errorEvent);
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"Error routing WebSocket message: {ex.Message}";
            _logger.LogErrorWithCaller(ex, errorMsg);
            var errorPayload = new ErrorPayload(_omsIdentifier, errorMsg);
            var errorEvent = new ErrorEvent(errorPayload);
            await _channel.SendAsync(errorEvent);
        }
    }
}
