using System;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenHFT.Oms.Api.WebSocket;

public class WebSocketChannel : IWebSocketChannel
{
    private readonly ILogger<WebSocketChannel> _logger;
    private System.Net.WebSockets.WebSocket? _socket;
    private readonly JsonSerializerOptions _jsonOptions;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public WebSocketChannel(ILogger<WebSocketChannel> logger)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }

    /// <summary>
    /// Called by the WebSocketHost to set the active socket connection.
    /// </summary>
    public void SetSocket(System.Net.WebSockets.WebSocket socket)
    {
        _socket = socket;
    }

    /// <summary>
    /// Called by the WebSocketHost when the connection is closed.
    /// </summary>
    public void ClearSocket()
    {
        _socket = null;
    }

    public async Task SendAsync(WebSocketMessage message)
    {
        if (!IsConnected)
        {
            _logger.LogWarning("Cannot send message. WebSocket is not connected.");
            return;
        }

        try
        {
            var messageJson = JsonSerializer.Serialize(message, message.GetType(), _jsonOptions);
            var bytes = System.Text.Encoding.UTF8.GetBytes(messageJson);
            await _socket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message via WebSocket.");
        }
    }
}