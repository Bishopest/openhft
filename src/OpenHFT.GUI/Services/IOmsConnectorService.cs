using System;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

// communication with a single OMS WebSocket server.
public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }
public interface IOmsConnectorService
{
    // --- Status Events ---
    event Action<ConnectionStatus> OnConnectionStatusChanged;
    ConnectionStatus CurrentStatus { get; }
    Uri? ConnectedServerUri { get; }
    // --- Data Events ---
    event Action<InstanceStatusEvent> OnInstanceStatusReceived;
    event Action<QuotePairUpdateEvent> OnQuotePairUpdateReceived;
    event Action<ErrorEvent> OnErrorReceived;
    event Action<AcknowledgmentEvent> OnAckReceived;

    // --- Connection Management ---
    Task ConnectAsync(Uri serverUri);
    Task DisconnectAsync();

    // --- Command Sending ---
    Task SendCommandAsync(WebSocketMessage command);
}
