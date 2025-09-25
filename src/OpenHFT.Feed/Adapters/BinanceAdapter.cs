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
    private readonly string[] _symbols;
    private readonly HashSet<string> _subscribedSymbols = new();
    private readonly Dictionary<string, long> _lastSequenceNumbers = new();
    private bool _isDisposed;

    // Binance WebSocket endpoints - using Futures API for better trading support
    private const string DefaultBaseUrl = "wss://fstream.binance.com/stream";
    private readonly string[] _defaultSymbols = { "btcusdt", "ethusdt" };

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public string Status => _webSocket?.State.ToString() ?? "Disconnected";
    public FeedStatistics Statistics { get; } = new();
    private readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    };

    public string ExchangeName => Exchange.BINANCE;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<FeedErrorEventArgs>? Error;
    public event EventHandler<MarketDataEvent>? MarketDataReceived;

    public BinanceAdapter(ILogger<BinanceAdapter> logger, string[] symbols, string? baseUrl = null)
    {
        _logger = logger;
        _baseUrl = baseUrl ?? DefaultBaseUrl;
        _symbols = symbols;
        Statistics.StartTime = DateTimeOffset.UtcNow;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(BinanceAdapter));
        if (IsConnected) return;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await ConnectWithRetryAsync(_symbols, _cancellationTokenSource.Token);
            Statistics.ReconnectCount++;
            _logger.LogInformationWithCaller("Connected to Binance WebSocket successfully");
            OnConnectionStateChanged(true, "Connected successfully");
            // Start receiving messages
            _receiveTask = Task.Run(() => ReceiveLoop(_cancellationTokenSource.Token));
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Failed to connect to Binance WebSocket");
            OnError(ex, "Connection failed");
            throw;
        }
    }

    private async Task ConnectWithRetryAsync(string[] symbols, CancellationToken cancellationToken)
    {
        var retryAttempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _webSocket = new ClientWebSocket();
                ConfigureWebSocket(_webSocket);

                var streamUrl = BuildStreamUrl(symbols);
                _logger.LogInformationWithCaller($"Connecting to {streamUrl} (attempt {retryAttempt + 1})");

                await _webSocket.ConnectAsync(new Uri(streamUrl), cancellationToken);

                _logger.LogInformationWithCaller($"Successfully connected to Binance WebSocket(state: {_webSocket.State.ToString()})");

                return;
            }
            catch (Exception ex) when (retryAttempt < RetryDelays.Length - 1)
            {
                var delay = RetryDelays[retryAttempt];

                _logger.LogErrorWithCaller(ex, $"Connection attempt {retryAttempt + 1} failed, retrying in {delay.TotalMilliseconds}ms");

                await Task.Delay(delay, cancellationToken);
                retryAttempt++;
            }
        }

        throw new InvalidOperationException($"Failed to connect after {RetryDelays.Length} attempts");
    }

    private void ConfigureWebSocket(ClientWebSocket webSocket)
    {
        // Configure WebSocket options for optimal performance
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        webSocket.Options.SetBuffer(8192, 8192); // 8KB buffers
    }

    private string BuildStreamUrl(string[] symbols)
    {
        var streams = new List<string>();

        foreach (var symbol in symbols)
        {
            var symbolLower = symbol.ToLowerInvariant();
            // Order book depth (20 levels, 100ms update frequency)
            streams.Add($"{symbolLower}@depth20@100ms");
            // Real-time aggregate trades
            streams.Add($"{symbolLower}@aggTrade");
        }

        return _baseUrl + string.Join("/", streams);
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
            _logger.LogInformationWithCaller("Disconnected from Binance WebSocket");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error during disconnect");
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
            _logger.LogInformationWithCaller("WebSocket receive loop cancelled");
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogWarningWithCaller("WebSocket connection closed prematurely");
            OnConnectionStateChanged(false, "Connection closed prematurely");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error in WebSocket receive loop");
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
            _logger.LogErrorWithCaller(ex, "Error processing message: {Message}", message);
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
