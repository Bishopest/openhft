using System;
using System.Text.Json;

namespace OpenHFT.Oms.Api.WebSocket;

/// <summary>
/// Defines the contract for a handler that processes a specific type of WebSocket command.
/// </summary>
public interface IWebSocketCommandHandler
{
    /// <summary>
    /// The message type string (e.g., "DEPLOY_STRATEGY") that this handler is responsible for.
    /// </summary>
    string CommandType { get; }

    /// <summary>
    /// Processes the incoming WebSocket command.
    /// </summary>
    /// <param name="messageElement">The root JsonElement of the incoming message.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task HandleAsync(JsonElement messageElement);
}