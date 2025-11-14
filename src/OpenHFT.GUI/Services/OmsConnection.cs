using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Utils;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

/// <summary>
/// Represents and manages a single, dedicated WebSocket connection to an OMS server.
/// This class is not a singleton; a new instance is created for each server connection.
/// </summary>
public class OmsConnection : IAsyncDisposable
{
    private readonly ILogger<OmsConnection> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;

    // --- Events for this specific connection ---
    public event Action<ConnectionStatus>? OnStatusChanged;
    public event Action<string>? OnMessageReceived; // Raw JSON for flexibility

    // --- Properties ---
    private readonly OmsServerConfig _serverConfig; // <-- Store the server config
    public OmsServerConfig ServerConfig => _serverConfig;
    private ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;
    public ConnectionStatus CurrentStatus
    {
        get => _currentStatus;
        private set
        {
            if (_currentStatus != value)
            {
                _currentStatus = value;
                OnStatusChanged?.Invoke(_currentStatus);
            }
        }
    }

    public OmsConnection(ILogger<OmsConnection> logger, JsonSerializerOptions jsonOptions, OmsServerConfig serverConfig)
    {
        _logger = logger;
        _jsonOptions = jsonOptions;
        _serverConfig = serverConfig;
    }

    public async Task ConnectAsync()
    {
        if (CurrentStatus == ConnectionStatus.Connected || CurrentStatus == ConnectionStatus.Connecting)
            return;

        _socket = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        CurrentStatus = ConnectionStatus.Connecting;

        try
        {
            _logger.LogInformationWithCaller($"Connecting to OMS at {ServerConfig.Url}...");
            await _socket.ConnectAsync(new Uri(ServerConfig.Url), _cts.Token);
            CurrentStatus = ConnectionStatus.Connected;
            _logger.LogInformationWithCaller($"Successfully connected to OMS at {ServerConfig.Url}.");
            _ = ReceiveLoop(); // Start listening in the background
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Failed to connect to OMS at {ServerConfig.Url}.");
            CurrentStatus = ConnectionStatus.Error;
            await CleanupAsync();
        }
    }

    private async Task ReceiveLoop()
    {
        if (_socket is null || _cts is null) return;
        await using var ms = new MemoryStream();
        var buffer = new ArraySegment<byte>(new byte[8192]);

        while (_socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            try
            {
                WebSocketReceiveResult result;
                ms.SetLength(0);
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformationWithCaller($"WebSocket connection to {ServerConfig.Url} closed by remote host.");
                        CurrentStatus = ConnectionStatus.Disconnected;
                        return;
                    }
                    if (buffer.Array != null)
                        await ms.WriteAsync(buffer.Array, buffer.Offset, result.Count, _cts.Token);

                } while (!result.EndOfMessage);

                var messageJson = Encoding.UTF8.GetString(ms.ToArray());
                OnMessageReceived?.Invoke(messageJson);
            }
            catch (OperationCanceledException)
            {
                break; // Expected on disconnect
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCaller(ex, $"An error occurred in the receive loop for {ServerConfig.Url}.");
                CurrentStatus = ConnectionStatus.Error;
                break;
            }
        }
    }

    public async Task SendCommandAsync(WebSocketMessage command)
    {
        if (CurrentStatus != ConnectionStatus.Connected || _socket is null)
        {
            _logger.LogWarningWithCaller($"Cannot send command to {ServerConfig.Url}, not connected.");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(command, command.GetType(), _jsonOptions);
            var buffer = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogDebug($"Sent command to {ServerConfig.Url}: {command.Type}");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Failed to send command to {ServerConfig.Url}.");
        }
    }

    private async Task CleanupAsync()
    {
        if (_socket is null) return;

        _cts?.Cancel();

        if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.Connecting)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", new CancellationTokenSource(2000).Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception during WebSocket close handshake.");
            }
        }
        _socket.Dispose();
        _cts?.Dispose();
        _socket = null;
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
        CurrentStatus = ConnectionStatus.Disconnected;
        _logger.LogInformationWithCaller($"Connection to {ServerConfig.Url} has been disposed.");
    }
}
