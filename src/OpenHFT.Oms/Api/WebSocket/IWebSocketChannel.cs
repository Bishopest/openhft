using System;

namespace OpenHFT.Oms.Api.WebSocket;

/// <summary>
/// Represents the single, active WebSocket communication channel to the central controller (e.g., Blazor app).
/// </summary>
public interface IWebSocketChannel
{
    /// <summary>
    /// Sends a message to the connected client.
    /// </summary>
    Task SendAsync(WebSocketMessage message);
}