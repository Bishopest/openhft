using System;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Books;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;
using OpenHFT.Hedging;
using OpenHFT.Quoting;
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
    private readonly IHedgerManager _hedgerManager;
    private readonly IBookManager _bookManager;
    private readonly IOrderRouter _orderRouter;
    // Add other event sources here, e.g., IPositionManager, IRiskManager
    private readonly string _omsIdentifier;

    public WebSocketNotificationService(
        ILogger<WebSocketNotificationService> logger,
        IWebSocketChannel channel,
        IQuotingInstanceManager quotingManager,
        IHedgerManager hedgerManager,
        IBookManager bookManager,
        IOrderRouter orderRouter,
        IConfiguration config
    )
    {
        _logger = logger;
        _channel = channel;
        _quotingManager = quotingManager;
        _hedgerManager = hedgerManager;
        _bookManager = bookManager;
        _orderRouter = orderRouter;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("WebSocket Notification Service is starting and subscribing to domain events.");

        // Subscribe to all relevant domain events
        _quotingManager.InstanceQuotePairCalculated += OnQuotePairCalculated;
        _quotingManager.InstanceParametersUpdated += OnInstanceParametersUpdated;
        _hedgerManager.HedgingParametersUpdated += OnHedgingParametersUpdated;
        _bookManager.BookElementUpdated += OnBookElementUpdated;
        _orderRouter.OrderStatusChanged += OnOrderStatusChanged;
        _orderRouter.OrderFilled += OnOrderFilled;
        // _positionManager.PositionChanged += OnPositionChanged; // Example for future extension

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("WebSocket Notification Service is stopping and unsubscribing from domain events.");

        // Unsubscribe to prevent memory leaks
        _quotingManager.InstanceQuotePairCalculated -= OnQuotePairCalculated;
        _quotingManager.InstanceParametersUpdated -= OnInstanceParametersUpdated;
        _hedgerManager.HedgingParametersUpdated -= OnHedgingParametersUpdated;
        _bookManager.BookElementUpdated -= OnBookElementUpdated;
        _orderRouter.OrderStatusChanged -= OnOrderStatusChanged;
        _orderRouter.OrderFilled -= OnOrderFilled;
        // _positionManager.PositionChanged -= OnPositionChanged;

        return Task.CompletedTask;
    }

    private void OnQuotePairCalculated(object? sender, QuotePair quotePair)
    {
        // Convert the domain event to a WebSocket message DTO
        var payload = new QuotePairUpdatePayload(_omsIdentifier, quotePair);
        var updateEvent = new QuotePairUpdateEvent(payload);

        // Use the channel to send the message. Fire-and-forget is appropriate here.
        _ = _channel.SendAsync(updateEvent);
    }

    private void OnInstanceParametersUpdated(object? sender, QuotingParameters newParameters)
    {
        _logger.LogInformationWithCaller($"Broadcasting instance parameter update for InstrumentId {newParameters.InstrumentId}");

        var instance = _quotingManager.GetInstance(newParameters.InstrumentId);
        if (instance == null) return;

        // InstanceStatusEvent를 재사용하여 UI에 최신 상태를 알림
        var payload = new InstanceStatusPayload
        {
            OmsIdentifier = _omsIdentifier,
            InstrumentId = instance.InstrumentId,
            IsActive = instance.IsActive,
            Parameters = newParameters
        };
        var statusEvent = new InstanceStatusEvent(payload);
        _ = _channel.SendAsync(statusEvent);
    }

    private void OnHedgingParametersUpdated(object? sender, HedgingParameters newParameters)
    {
        _logger.LogInformationWithCaller($"Broadcasting instance hedging parameter update for Quote InstrumentID {newParameters.QuotingInstrumentId} Hedge InstrumentId {newParameters.InstrumentId}");

        var hedger = _hedgerManager.GetHedger(newParameters.QuotingInstrumentId);
        if (hedger == null) return;

        var payload = new HedgingStatusPayload
        {
            OmsIdentifier = _omsIdentifier,
            IsActive = hedger.IsActive,
            Parameters = newParameters
        };
        var statusEvent = new HedgingStatusEvent(payload);
        _ = _channel.SendAsync(statusEvent);
    }

    private void OnBookElementUpdated(object? sender, BookElement element)
    {
        _logger.LogInformationWithCaller($"Broadcasting book element update for InstrumentId {element.InstrumentId}");
        var elements = new List<BookElement>() { element };
        var payload = new BookUpdatePayload(_omsIdentifier, Enumerable.Empty<BookInfo>(), elements);
        var updateEvent = new BookUpdateEvent(payload);
        _ = _channel.SendAsync(updateEvent);
    }

    private void OnOrderStatusChanged(object? sender, OrderStatusReport report)
    {
        if (report.Status == OrderStatus.Pending)
        {
            _logger.LogInformationWithCaller($"Broadcasting order status update for the order of which last report {report}");
            if (sender is Order order)
            {
                var currentFills = order.Fills;
                var sb = new StringBuilder();
                sb.AppendLine($"--- Order {report.ClientOrderId} Fills ({currentFills.Count} total) ---");
                foreach (var fill in currentFills)
                {
                    sb.AppendLine($"  - {fill.ToString()}");
                }
                sb.AppendLine("-------------------------------------------------");
                _logger.LogInformationWithCaller(sb.ToString());
            }
        }
        var payload = new ActiveOrdersPayload(_omsIdentifier, new List<OrderStatusReport>() { report });
        var updateEvent = new ActiveOrdersListEvent(payload);
        _ = _channel.SendAsync(updateEvent);
    }

    private void OnOrderFilled(object? sender, Fill fill)
    {
        _logger.LogTrace("Broadcasting order fill for CID {ClientOrderId}", fill.ClientOrderId);
        var payload = new FillsPayload(_omsIdentifier, new List<Fill>() { fill });
        var fillEvent = new FillsListEvent(payload);
        _ = _channel.SendAsync(fillEvent);
    }
    // private void OnPositionChanged(object? sender, Position newPosition) { ... }
}