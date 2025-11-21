using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

public class OmsConnectorService : IOmsConnectorService, IAsyncDisposable
{
    private readonly ILogger<OmsConnectorService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<string, OmsConnection> _connections = new();

    public event Action<(OmsServerConfig Server, ConnectionStatus Status)>? OnConnectionStatusChanged;
    public event Action<string>? OnMessageReceived;

    public OmsConnectorService(ILogger<OmsConnectorService> logger, IServiceProvider serviceProvider, JsonSerializerOptions jsonOptions)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _jsonOptions = jsonOptions; // Receive pre-configured options
    }

    public ConnectionStatus GetStatus(OmsServerConfig server)
    {
        return _connections.TryGetValue(server.Url, out var connection)
            ? connection.CurrentStatus
            : ConnectionStatus.Disconnected;
    }

    public async Task ConnectAsync(OmsServerConfig server)
    {
        var connection = _connections.GetOrAdd(server.Url, _ =>
        {
            _logger.LogInformationWithCaller($"Creating new connection object for {server.OmsIdentifier} ({server.Url})");
            // Create a new OmsConnection instance using the service provider
            // This correctly resolves its dependencies (logger, jsonOptions).
            var connectionLogger = _serviceProvider.GetRequiredService<ILogger<OmsConnection>>();
            var newConn = new OmsConnection(connectionLogger, _jsonOptions, server);

            // Bubble up events from the specific connection to the central service
            newConn.OnStatusChanged += (status) => OnConnectionStatusChanged?.Invoke((server, status));
            newConn.OnMessageReceived += (json) => OnMessageReceived?.Invoke(json);

            return newConn;
        });

        await connection.ConnectAsync();
    }

    public async Task DisconnectAsync(OmsServerConfig server)
    {
        if (_connections.TryRemove(server.Url, out var connection))
        {
            await connection.DisposeAsync();
        }
    }

    public async Task SendCommandAsync(OmsServerConfig server, WebSocketMessage command)
    {
        if (_connections.TryGetValue(server.Url, out var connection))
        {
            _logger.LogWarningWithCaller($"Sending command to {server.OmsIdentifier} {command} ({server.Url})");
            await connection.SendCommandAsync(command);
        }
        else
        {
            _logger.LogWarningWithCaller($"Attempted to send command to a non-existent connection for {server.OmsIdentifier}.");
        }
    }

    public IEnumerable<OmsServerConfig> GetConnectedServers()
    {
        return _connections.Where(kvp => kvp.Value.CurrentStatus == ConnectionStatus.Connected).Select(kvp => kvp.Value.ServerConfig);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var key in _connections.Keys)
        {
            if (_connections.TryRemove(key, out var connection))
            {
                await connection.DisposeAsync();
            }
        }
    }
}
