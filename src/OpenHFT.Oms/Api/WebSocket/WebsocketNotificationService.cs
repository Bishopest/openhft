using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Oms.Api.WebSocket;

/// <summary>
/// An IHostedService that listens to internal domain events (like quote updates)
/// and broadcasts them to the connected WebSocket client.
/// </summary>
public class WebSocketNotificationService : IHostedService
{
    private readonly ILogger<WebSocketNotificationService> _logger;
    private readonly IWebSocketChannel _channel;
    private readonly IQuotingInstanceManager _quotingManager;
    // Add other event sources here, e.g., IPositionManager, IRiskManager

    public WebSocketNotificationService(
        ILogger<WebSocketNotificationService> logger,
        IWebSocketChannel channel,
        IQuotingInstanceManager quotingManager)
    {
        _logger = logger;
        _channel = channel;
        _quotingManager = quotingManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("WebSocket Notification Service is starting and subscribing to domain events.");

        // Subscribe to all relevant domain events
        _quotingManager.InstanceQuotePairCalculated += OnQuotePairCalculated;
        // _positionManager.PositionChanged += OnPositionChanged; // Example for future extension

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("WebSocket Notification Service is stopping and unsubscribing from domain events.");

        // Unsubscribe to prevent memory leaks
        _quotingManager.InstanceQuotePairCalculated -= OnQuotePairCalculated;
        // _positionManager.PositionChanged -= OnPositionChanged;

        return Task.CompletedTask;
    }

    private void OnQuotePairCalculated(object? sender, QuotePair quotePair)
    {
        // Convert the domain event to a WebSocket message DTO
        var updateEvent = new QuotePairUpdateEvent(quotePair);

        // Use the channel to send the message. Fire-and-forget is appropriate here.
        _ = _channel.SendAsync(updateEvent);
    }

    // private void OnPositionChanged(object? sender, Position newPosition) { ... }
}