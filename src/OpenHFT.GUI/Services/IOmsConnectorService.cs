using System;
using OpenHFT.Core.Configuration;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

// communication with a single OMS WebSocket server.
public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }
public interface IOmsConnectorService
{
    event Action<(OmsServerConfig Server, ConnectionStatus Status)>? OnConnectionStatusChanged;
    event Action<string>? OnMessageReceived; // Centralized message event

    ConnectionStatus GetStatus(OmsServerConfig server);
    Task ConnectAsync(OmsServerConfig server);
    Task DisconnectAsync(OmsServerConfig server);
    Task SendCommandAsync(OmsServerConfig server, WebSocketMessage command);
    IEnumerable<OmsServerConfig> GetConnectedServers();
}
