using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed.Adapters;

/// <summary>
/// Binance WebSocket feed adapter for real-time market data
/// Supports both individual symbol streams and combined streams
/// </summary>
public class BinanceAdapter : IFeedAdapter
{
    private readonly ILogger<BinanceAdapter> _logger;
    private readonly string _baseUrl;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveTask;
    private readonly HashSet<string> _subscribedSymbols = new();
    private readonly Dictionary<string, long> _lastSequenceNumbers = new();
    private bool _isDisposed;

    // Binance WebSocket endpoints - using Futures API for better trading support
    private const string DefaultBaseUrl = "wss://fstream.binance.com/stream";
    private readonly string[] _defaultSymbols = { "btcusdt", "ethusdt", "adausdt" };

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public string Status => _webSocket?.State.ToString() ?? "Disconnected";
    public FeedStatistics Statistics { get; } = new();

    public string ExchangeName => Exchange.BINANCE;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<FeedErrorEventArgs>? Error;
    public event EventHandler<MarketDataEvent>? MarketDataReceived;

    public BinanceAdapter(ILogger<BinanceAdapter> logger, string? baseUrl = null)
    {
        _logger = logger;
        _baseUrl = baseUrl ?? DefaultBaseUrl;
        Statistics.StartTime = DateTimeOffset.UtcNow;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(BinanceAdapter));
        if (IsConnected) return;

        try
        {
            _webSocket = new ClientWebSocket();
            _cancellationTokenSource = new CancellationTokenSource();

            // Build combined stream URL with depth and trade streams for configured symbols
            var streams = new List<string>();
            foreach (var symbol in _defaultSymbols)
            {
                streams.Add($"{symbol}@depth20@100ms"); // Order book depth updates every 100ms
                streams.Add($"{symbol}@aggTrade");      // Aggregated trades
            }

            var streamParams = string.Join("/", streams);
            var connectUri = new Uri($"{_baseUrl}?streams={streamParams}");

            _logger.LogInformation("Connecting to Binance WebSocket at {Url}", connectUri);
            await _webSocket.ConnectAsync(connectUri, cancellationToken);

            Statistics.ReconnectCount++;
            _logger.LogInformation("Connected to Binance WebSocket successfully");

            OnConnectionStateChanged(true, "Connected successfully");

            // Start receiving messages
            _receiveTask = ReceiveLoop(_cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Binance WebSocket");
            OnError(ex, "Connection failed");
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return;

        try
        {
            _cancellationTokenSource?.Cancel();

            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect requested", cancellationToken);
            }

            if (_receiveTask != null)
            {
                await _receiveTask;
            }

            OnConnectionStateChanged(false, "Disconnected by request");
            _logger.LogInformation("Disconnected from Binance WebSocket");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disconnect");
            OnError(ex, "Disconnect error");
        }
        finally
        {
            _webSocket?.Dispose();
            _webSocket = null;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    public async Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to WebSocket");

        var symbolList = symbols.ToList();
        foreach (var symbol in symbolList)
        {
            var normalizedSymbol = symbol.ToLower();
            if (_subscribedSymbols.Add(normalizedSymbol))
            {
                // Subscribe to depth stream for order book updates
                var subscribeMessage = JsonSerializer.Serialize(new
                {
                    method = "SUBSCRIBE",
                    @params = new[] { $"{normalizedSymbol}@depth@100ms" },
                    id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });

                await SendMessage(subscribeMessage, cancellationToken);
                _logger.LogInformation("Subscribed to {Symbol} depth stream", normalizedSymbol);
            }
        }
    }

    public async Task UnsubscribeAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to WebSocket");

        var symbolList = symbols.ToList();
        foreach (var symbol in symbolList)
        {
            var normalizedSymbol = symbol.ToLower();
            if (_subscribedSymbols.Remove(normalizedSymbol))
            {
                var unsubscribeMessage = JsonSerializer.Serialize(new
                {
                    method = "UNSUBSCRIBE",
                    @params = new[] { $"{normalizedSymbol}@depth@100ms" },
                    id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });

                await SendMessage(unsubscribeMessage, cancellationToken);
                _logger.LogInformation("Unsubscribed from {Symbol} depth stream", normalizedSymbol);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // WebSocket starts receiving automatically after connection
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return DisconnectAsync(cancellationToken);
    }

    private async Task SendMessage(string message, CancellationToken cancellationToken)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected");

        var bytes = Encoding.UTF8.GetBytes(message);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var messageBuffer = new StringBuilder();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                messageBuffer.Clear();

                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("WebSocket closed by remote endpoint");
                        OnConnectionStateChanged(false, "Remote endpoint closed connection");
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        Statistics.BytesReceived += result.Count;
                    }
                } while (!result.EndOfMessage);

                if (messageBuffer.Length > 0)
                {
                    await ProcessMessage(messageBuffer.ToString());
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("WebSocket receive loop cancelled");
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogWarning("WebSocket connection closed prematurely");
            OnConnectionStateChanged(false, "Connection closed prematurely");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WebSocket receive loop");
            OnError(ex, "Receive loop error");
            OnConnectionStateChanged(false, "Error in receive loop");
        }
    }

    private async Task ProcessMessage(string message)
    {
        try
        {
            Statistics.MessagesReceived++;
            Statistics.LastMessageTime = DateTimeOffset.UtcNow;

            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;

            // Check if this is a subscription response
            if (root.TryGetProperty("result", out _) || root.TryGetProperty("id", out _))
            {
                _logger.LogDebug("Received subscription response: {Message}", message);
                return;
            }

            // Process depth update
            if (root.TryGetProperty("stream", out var streamElement) &&
                root.TryGetProperty("data", out var dataElement))
            {
                var stream = streamElement.GetString();
                if (stream?.Contains("@depth") == true)
                {
                    await ProcessDepthUpdate(dataElement, stream);
                }
            }
            else if (root.TryGetProperty("e", out var eventTypeElement))
            {
                // Direct depth update (single stream)
                var eventType = eventTypeElement.GetString();
                if (eventType == "depthUpdate")
                {
                    await ProcessDepthUpdate(root);
                }
            }

            Statistics.MessagesProcessed++;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message: {Message}", message);
            Statistics.MessagesDropped++;
            OnError(ex, "Message processing error");
        }
    }

    private async Task ProcessDepthUpdate(JsonElement data, string? stream = null)
    {
        try
        {
            var symbol = stream?.Split('@')[0].ToUpper() ??
                        (data.TryGetProperty("s", out var symbolElement) ? symbolElement.GetString()?.ToUpper() : null);

            if (string.IsNullOrEmpty(symbol))
            {
                _logger.LogWarning("Missing symbol in depth update");
                return;
            }

            var symbolId = SymbolUtils.GetSymbolId(symbol);
            var timestamp = TimestampUtils.GetTimestampMicros();

            // Check sequence number for gap detection
            if (data.TryGetProperty("u", out var lastUpdateIdElement))
            {
                var currentSequence = lastUpdateIdElement.GetInt64();
                if (_lastSequenceNumbers.TryGetValue(symbol, out var lastSequence))
                {
                    if (currentSequence != lastSequence + 1)
                    {
                        _logger.LogDebug("Sequence gap detected for {Symbol}: expected {Expected}, received {Received}",
                            symbol, lastSequence + 1, currentSequence);
                        Statistics.SequenceGaps++;
                    }
                }
                _lastSequenceNumbers[symbol] = currentSequence;
            }

            // Process bids
            if (data.TryGetProperty("b", out var bidsElement))
            {
                foreach (var bid in bidsElement.EnumerateArray())
                {
                    if (bid.GetArrayLength() >= 2 &&
                        decimal.TryParse(bid[0].GetString(), out var price) &&
                        decimal.TryParse(bid[1].GetString(), out var quantity))
                    {
                        var eventKind = quantity == 0 ? EventKind.Delete : EventKind.Update;
                        var marketDataEvent = new MarketDataEvent(
                            sequence: _lastSequenceNumbers.GetValueOrDefault(symbol),
                            timestamp: timestamp,
                            side: Side.Buy,
                            priceTicks: PriceUtils.ToTicks(price),
                            quantity: (long)(quantity * 100000000), // Convert to base units
                            kind: eventKind,
                            symbolId: symbolId
                        );

                        OnMarketDataReceived(marketDataEvent);
                    }
                }
            }

            // Process asks
            if (data.TryGetProperty("a", out var asksElement))
            {
                foreach (var ask in asksElement.EnumerateArray())
                {
                    if (ask.GetArrayLength() >= 2 &&
                        decimal.TryParse(ask[0].GetString(), out var price) &&
                        decimal.TryParse(ask[1].GetString(), out var quantity))
                    {
                        var eventKind = quantity == 0 ? EventKind.Delete : EventKind.Update;
                        var marketDataEvent = new MarketDataEvent(
                            sequence: _lastSequenceNumbers.GetValueOrDefault(symbol),
                            timestamp: timestamp,
                            side: Side.Sell,
                            priceTicks: PriceUtils.ToTicks(price),
                            quantity: (long)(quantity * 100000000), // Convert to base units
                            kind: eventKind,
                            symbolId: symbolId
                        );

                        OnMarketDataReceived(marketDataEvent);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing depth update");
            throw;
        }
    }

    protected virtual void OnConnectionStateChanged(bool isConnected, string? reason = null)
    {
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(isConnected, reason));
    }

    protected virtual void OnError(Exception exception, string? context = null)
    {
        Error?.Invoke(this, new FeedErrorEventArgs(exception, context));
    }

    protected virtual void OnMarketDataReceived(MarketDataEvent marketDataEvent)
    {
        MarketDataReceived?.Invoke(this, marketDataEvent);
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        try
        {
            DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disposal");
        }

        _webSocket?.Dispose();
        _cancellationTokenSource?.Dispose();
        _isDisposed = true;
    }
}
