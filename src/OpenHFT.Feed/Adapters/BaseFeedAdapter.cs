using System;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Exceptions;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed.Adapters;

public abstract class BaseFeedAdapter : IFeedAdapter
{
    protected readonly ILogger _logger;
    protected readonly IInstrumentRepository _instrumentRepository;
    protected ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly object _connectionLock = new();
    private Task? _receiveTask;
    private bool _isDisposed;
    private readonly object _subscriptionLock = new();
    private readonly HashSet<Instrument> _subscribedInsts = new();

    private readonly TimeSpan[] _retryDelays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15)
    };

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public string Status => _webSocket?.State.ToString() ?? "Disconnected";

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<FeedErrorEventArgs>? Error;
    public event EventHandler<MarketDataEvent>? MarketDataReceived;

    public ProductType ProductType { get; }

    protected BaseFeedAdapter(ILogger logger, ProductType type, IInstrumentRepository instrumentRepository)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instrumentRepository = instrumentRepository;
        ProductType = type;
    }

    #region Public Methods (IFeedAdapter Implementation)
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().Name);
        if (IsConnected) return;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await ConnectWithRetryAsync(_cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Connection attempt was cancelled.");
        }
        catch (Exception ex)
        {
            var fex = new FeedConnectionException(SourceExchange, new Uri(GetBaseUrl()), "Failed to connect after all retries.", ex);
            _logger.LogError(fex, "Failed to connect to {Exchange} WebSocket.", SourceExchange);
            OnError(new FeedErrorEventArgs(fex, "Connection Failure"));
            throw fex;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_connectionLock)
        {
            if (!IsConnected && _receiveTask == null) return;
        }

        _logger.LogInformationWithCaller($"Disconnecting from {SourceExchange}_{ProductType} WebSocket...");

        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
        }

        if (_receiveTask != null)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                await _receiveTask.WaitAsync(linkedCts.Token);
                _logger.LogInformation("Receive task for {Exchange} completed.", SourceExchange);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Disconnection was cancelled by the caller for {Exchange}.", SourceExchange);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timed out waiting for the receive task to complete for {Exchange}.", SourceExchange);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An exception occurred while waiting for the receive task to complete for {Exchange}.", SourceExchange);
            }
            finally
            {
                _receiveTask = null;
            }
        }

        CleanupConnection();
        _logger.LogInformationWithCaller($"Successfully disconnected from {SourceExchange} WebSocket.");
    }

    public async Task SubscribeAsync(IEnumerable<Instrument> symbols, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Cannot subscribe when not connected.");

        var symbolsToSubscribe = new List<Instrument>();
        lock (_subscriptionLock)
        {
            foreach (var symbol in symbols)
            {
                if (_subscribedInsts.Add(symbol))
                {
                    symbolsToSubscribe.Add(symbol);
                }
            }
        }

        if (symbolsToSubscribe.Any())
        {
            _logger.LogInformationWithCaller($"Subscribing to {symbolsToSubscribe.Count} new symbols on {SourceExchange}: {string.Join(", ", symbolsToSubscribe.Select(s => s.Symbol))}");
            await DoSubscribeAsync(symbolsToSubscribe, cancellationToken);
        }
    }

    public async Task UnsubscribeAsync(IEnumerable<Instrument> symbols, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Cannot unsubscribe when not connected.");

        var symbolsToUnsubscribe = new List<Instrument>();
        lock (_subscriptionLock)
        {
            foreach (var symbol in symbols)
            {
                if (_subscribedInsts.Remove(symbol))
                {
                    symbolsToUnsubscribe.Add(symbol);
                }
            }
        }

        if (symbolsToUnsubscribe.Any())
        {
            _logger.LogInformationWithCaller($"Unsubscribing from {symbolsToUnsubscribe.Count} symbols on {SourceExchange}: {string.Join(", ", symbolsToUnsubscribe.Select(k => k.Symbol))}");
            await DoUnsubscribeAsync(symbolsToUnsubscribe, cancellationToken);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Connection and Receive Logic

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        if (IsConnected || _receiveTask != null)
        {
            await DisconnectAsync(CancellationToken.None);
        }

        int retryAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                CleanupConnection();

                _webSocket = new ClientWebSocket();
                ConfigureWebSocket(_webSocket);

                var baseUrl = GetBaseUrl();
                _logger.LogInformationWithCaller($"Connecting to {baseUrl} (Attempt {retryAttempt + 1})");

                await _webSocket.ConnectAsync(new Uri(baseUrl), cancellationToken);

                if (_webSocket.State == WebSocketState.Open)
                {
                    _logger.LogInformationWithCaller($"Successfully connected to {SourceExchange} WebSocket.");
                    OnConnectionStateChanged(true, "Connected Successfully");
                    _receiveTask = Task.Run(() => ReceiveLoop(cancellationToken), cancellationToken);
                    return;
                }
            }
            catch (Exception ex) when (retryAttempt < _retryDelays.Length)
            {
                var delay = _retryDelays[retryAttempt];
                _logger.LogErrorWithCaller(ex, $"Connection attempt {retryAttempt + 1} for {SourceExchange} failed. Retrying in {delay.TotalSeconds}s...");
                await Task.Delay(delay, cancellationToken);
                retryAttempt++;
            }
        }

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("Connection was cancelled.");

        throw new Exception($"Failed to connect to {SourceExchange} after {retryAttempt} attempts.");
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    var segment = new ArraySegment<byte>(buffer);
                    result = await _webSocket.ReceiveAsync(segment, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, $"Connection closed by remote host. Status: {result.CloseStatus}, Description: {result.CloseStatusDescription}");
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);

                if (ms.Length > 0)
                {
                    await ProcessMessage(ms);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformationWithCaller($"{SourceExchange} ReceiveLoop was cancelled.");
        }
        catch (WebSocketException ex)
        {
            _logger.LogErrorWithCaller(ex, $"{SourceExchange} WebSocket connection closed unexpectedly. Reason: {ex.Message}. Attempting to reconnect...");
            OnConnectionStateChanged(false, "Connection Lost");
            _ = Task.Run(() => ConnectWithRetryAsync(_cancellationTokenSource?.Token ?? CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Unhandled exception in {SourceExchange} ReceiveLoop.");
            OnError(new FeedErrorEventArgs(new FeedReceiveException(SourceExchange, "Unhandled exception in receive loop", ex), null));
        }
    }

    protected async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        if (_webSocket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }
        var bytes = Encoding.UTF8.GetBytes(message);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    #endregion

    #region Abstract & Virtual Methods for Subclasses

    public abstract ExchangeEnum SourceExchange { get; }

    /// <summary>
    /// Gets the base WebSocket URL for the exchange.
    /// </summary>
    protected abstract string GetBaseUrl();

    protected abstract void ConfigureWebsocket(ClientWebSocket websocket);

    /// <summary>
    /// Processes a raw message received from the WebSocket.
    /// </summary>
    protected abstract Task ProcessMessage(MemoryStream messageStream);

    /// <summary>
    /// Sends subscription messages to the WebSocket for new symbols.
    /// </summary>
    protected abstract Task DoSubscribeAsync(IEnumerable<Instrument> insts, CancellationToken cancellationToken);

    /// <summary>
    /// Sends unsubscription messages to the WebSocket.
    /// </summary>
    protected abstract Task DoUnsubscribeAsync(IEnumerable<Instrument> insts, CancellationToken cancellationToken);

    /// <summary>
    /// Allows subclasses to apply specific WebSocket configurations.
    /// </summary>
    protected virtual void ConfigureWebSocket(ClientWebSocket webSocket)
    {
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    #endregion

    #region Helper & Cleanup Methods

    protected virtual void OnConnectionStateChanged(bool isConnected, string reason)
    {
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(isConnected, SourceExchange, reason));
    }

    protected virtual void OnError(FeedErrorEventArgs e)
    {
        Error?.Invoke(this, e);
    }

    protected virtual void OnMarketDataReceived(MarketDataEvent e)
    {
        MarketDataReceived?.Invoke(this, e);
    }

    private void CleanupConnection()
    {
        lock (_connectionLock)
        {
            if (_webSocket != null)
            {
                if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.Connecting)
                {
                    try
                    {
                        _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Cleaning up", new CancellationTokenSource(200).Token).Wait();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarningWithCaller(ex.Message);
                    }
                }
                _webSocket?.Dispose();
                _webSocket = null;
            }

            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }

            _receiveTask?.Wait(TimeSpan.FromSeconds(2));
            CleanupConnection();
        }

        _isDisposed = true;
    }

    #endregion
}
