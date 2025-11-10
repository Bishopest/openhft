using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenHFT.Core.Utils;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

public class OmsConnectorService : IOmsConnectorService, IAsyncDisposable
{
    private readonly ILogger<OmsConnectorService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;

    public event Action<ConnectionStatus>? OnConnectionStatusChanged;
    public event Action<InstanceStatusEvent>? OnInstanceStatusReceived;
    public event Action<ErrorEvent>? OnErrorReceived;
    public event Action<AcknowledgmentEvent>? OnAckReceived;

    private ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;
    public ConnectionStatus CurrentStatus
    {
        get => _currentStatus;
        private set
        {
            _currentStatus = value;
            OnConnectionStatusChanged?.Invoke(_currentStatus);
        }
    }

    public OmsConnectorService(ILogger<OmsConnectorService> logger)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            // For SENDING data to the server in camelCase
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

            // For RECEIVING data from the server, be flexible with casing.
            // This will correctly map "instrumentId" (from server) to "InstrumentId" (in C#).
            PropertyNameCaseInsensitive = true,

            // For RECEIVING enum values as strings (e.g., "Spot" instead of 0)
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
    }

    public async Task ConnectAsync(Uri serverUri)
    {
        if (CurrentStatus == ConnectionStatus.Connected || CurrentStatus == ConnectionStatus.Connecting)
            return;

        _socket = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        CurrentStatus = ConnectionStatus.Connecting;

        try
        {
            await _socket.ConnectAsync(serverUri, _cts.Token);
            CurrentStatus = ConnectionStatus.Connected;
            _logger.LogInformationWithCaller($"Successfully connected to OMS at {serverUri}");
            _ = ReceiveLoop(); // Start listening in the background
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Failed to connect to OMS.");
            CurrentStatus = ConnectionStatus.Error;
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
                    if (result.MessageType == WebSocketMessageType.Close) { await DisconnectAsync(); return; }
                    if (buffer.Array != null) await ms.WriteAsync(buffer.Array, buffer.Offset, result.Count, _cts.Token);
                } while (!result.EndOfMessage);

                var messageJson = Encoding.UTF8.GetString(ms.ToArray());
                await HandleIncomingMessage(messageJson);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogErrorWithCaller(ex, "An error occurred in the receive loop.");
                CurrentStatus = ConnectionStatus.Error;
                break;
            }
        }
    }

    private async Task HandleIncomingMessage(string json)
    {
        _logger.LogDebug($"Received Websocket message: {json}");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.GetString() is not { } type)
            {
                _logger.LogWarningWithCaller("Received message without a 'type' property.");
                return;
            }

            // Now, deserialize the full object based on its type.
            switch (type)
            {
                case "INSTANCE_STATUS":
                    // --- USE THE CONFIGURED OPTIONS FOR DESERIALIZATION ---
                    var statusEvent = JsonSerializer.Deserialize<InstanceStatusEvent>(json, _jsonOptions);
                    if (statusEvent != null) OnInstanceStatusReceived?.Invoke(statusEvent);
                    break;
                case "ERROR":
                    var errorEvent = JsonSerializer.Deserialize<ErrorEvent>(json, _jsonOptions);
                    if (errorEvent != null) OnErrorReceived?.Invoke(errorEvent);
                    break;
                case "ACK":
                    var ackEvent = JsonSerializer.Deserialize<AcknowledgmentEvent>(json, _jsonOptions);
                    if (ackEvent != null) OnAckReceived?.Invoke(ackEvent);
                    break;
                default:
                    _logger.LogWarning("Received unknown WebSocket message type: {Type}", type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize or handle incoming message.");
        }
        await Task.CompletedTask; // To satisfy async method signature if needed
    }

    public async Task SendCommandAsync(WebSocketMessage command)
    {
        if (CurrentStatus != ConnectionStatus.Connected || _socket is null)
        {
            _logger.LogWarningWithCaller("Cannot send command, not connected.");
            return;
        }

        var json = JsonSerializer.Serialize(command, command.GetType(), _jsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        _logger.LogInformationWithCaller($"Sent command: {command.Type}");
    }

    public async Task DisconnectAsync()
    {
        if (_socket is null) return;
        _cts?.Cancel();

        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None);
        }
        _socket.Dispose();
        _cts?.Dispose();
        CurrentStatus = ConnectionStatus.Disconnected;
        _logger.LogInformationWithCaller("Disconnected from OMS.");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
