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
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    //CTS to track message inactivity
    private CancellationTokenSource? _inactivityCts;
    // TCS to wait pong messages
    private TaskCompletionSource<bool> _pongTcs;

    protected readonly ILogger _logger;
    protected readonly IInstrumentRepository _instrumentRepository;
    protected ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly object _connectionLock = new();
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

    public ProductType ProdType { get; }

    protected BaseFeedAdapter(ILogger logger, ProductType type, IInstrumentRepository instrumentRepository)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instrumentRepository = instrumentRepository;
        ProdType = type;
    }

    #region Public Methods (IFeedAdapter Implementation)
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().Name);
        if (IsConnected) return;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await ConnectWithRetryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
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

        _logger.LogInformationWithCaller($"Disconnecting from {SourceExchange}_{ProdType} WebSocket...");

        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
        }

        var tasksToWait = new List<Task>();
        if (_receiveTask != null) tasksToWait.Add(_receiveTask);
        if (_heartbeatTask != null) tasksToWait.Add(_heartbeatTask);

        if (tasksToWait.Any())
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Task.WhenAll(tasksToWait).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                _logger.LogInformationWithCaller($"Receive and Heartbeat tasks for {SourceExchange} completed.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarningWithCaller($"Timed out waiting for tasks to complete for {SourceExchange}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCaller(ex, $"An exception occurred while waiting for tasks to complete for {SourceExchange}.");
            }
            finally
            {
                _receiveTask = null;
                _heartbeatTask = null;
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
                _webSocket = new ClientWebSocket();
                ConfigureWebSocket(_webSocket);

                var baseUrl = GetBaseUrl();
                _logger.LogInformationWithCaller($"Connecting to {baseUrl} (Attempt {retryAttempt + 1})");

                await _webSocket.ConnectAsync(new Uri(baseUrl), cancellationToken).ConfigureAwait(false);

                if (_webSocket.State == WebSocketState.Open)
                {
                    _logger.LogInformationWithCaller($"Successfully connected to {SourceExchange} WebSocket.");
                    OnConnectionStateChanged(true, "Connected Successfully");
                    _receiveTask = Task.Run(() => ReceiveLoop(cancellationToken), cancellationToken);

                    if (IsHeartbeatEnabled)
                    {
                        _heartbeatTask = Task.Run(() => HeartbeatLoop(cancellationToken), cancellationToken);
                    }
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
                    result = await _webSocket.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, $"Connection closed by remote host. Status: {result.CloseStatus}, Description: {result.CloseStatusDescription}");
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);

                if (ms.Length > 0)
                {
                    // Any message (data or pong) resets the inactivity timer.
                    ResetInactivityTimer();

                    if (IsPongMessage(ms))
                    {
                        _logger.LogInformationWithCaller($"Pong received from {SourceExchange}.");
                        _pongTcs?.TrySetResult(true);
                        continue;
                    }

                    await ProcessMessage(ms).ConfigureAwait(false);

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
            _ = Task.Run(() => ConnectWithRetryAsync(_cancellationTokenSource?.Token ?? CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Unhandled exception in {SourceExchange} ReceiveLoop.");
            OnError(new FeedErrorEventArgs(new FeedReceiveException(SourceExchange, "Unhandled exception in receive loop", ex), null));
        }
    }

    private async Task HeartbeatLoop(CancellationToken cancellationToken)
    {
        var inactivityTimeout = GetInactivityTimeout();
        var pingTimeout = GetPingTimeout();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for a period of inactivity. This delay will be cancelled 
                // by ResetInactivityTimer if any message is received.
                await Task.Delay(inactivityTimeout, _inactivityCts!.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // This is the expected behavior when a message is received.
                // The timer was reset, so we just loop again.
                continue;
            }

            if (cancellationToken.IsCancellationRequested) break;

            // If we reach here, the delay completed without cancellation,
            // meaning no messages were received for the duration of inactivityTimeout.
            _logger.LogInformationWithCaller($"No message received for {inactivityTimeout.TotalSeconds}s. Sending ping to {SourceExchange}.");

            try
            {
                _pongTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pingMessage = GetPingMessage();
                if (pingMessage != null)
                {
                    await SendMessageAsync(pingMessage, cancellationToken).ConfigureAwait(false);
                }

                // Wait for the pong response or a timeout.
                using var timeoutCts = new CancellationTokenSource(pingTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var pongTask = _pongTcs.Task;
                var completedTask = await Task.WhenAny(pongTask, Task.Delay(pingTimeout, linkedCts.Token)).ConfigureAwait(false);

                if (completedTask != pongTask || !pongTask.Result)
                {
                    _logger.LogWarningWithCaller($"Did not receive a pong from {SourceExchange} within {pingTimeout.TotalSeconds}s. Connection is considered stale. Triggering reconnect.");
                    await CloseSocketForReconnectAsync().ConfigureAwait(false);
                    return; // Exit the heartbeat loop.
                }

                _logger.LogInformationWithCaller($"Successfully received pong from {SourceExchange}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogErrorWithCaller(ex, $"An error occurred in the heartbeat loop for {SourceExchange}. Triggering reconnect.");
                await CloseSocketForReconnectAsync().ConfigureAwait(false);
                return; // Exit the heartbeat loop.
            }
        }
        _logger.LogInformationWithCaller($"Heartbeat loop for {SourceExchange} has stopped.");
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

    /// <summary>
    /// Override and return false for adapters that handle keep-alives at the protocol level (e.g., Binance)
    /// or do not require manual heartbeats.
    /// </summary>
    protected virtual bool IsHeartbeatEnabled => false;

    /// <summary>
    /// Gets the ping message to send for a heartbeat. Can be null if not supported.
    /// </summary>
    protected abstract string? GetPingMessage();

    /// <summary>
    /// Checks if the received message is a pong response.
    /// Note: The implementation should not dispose the stream and should reset its position if read.
    /// </summary>
    protected abstract bool IsPongMessage(MemoryStream messageStream);

    /// <summary>
    /// Defines the duration of inactivity before a ping is sent.
    /// </summary>
    protected virtual TimeSpan GetInactivityTimeout() => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Defines how long to wait for a pong after a ping is sent.
    /// </summary>
    protected virtual TimeSpan GetPingTimeout() => TimeSpan.FromSeconds(5);

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

    private void ResetInactivityTimer()
    {
        try
        {
            _inactivityCts?.Cancel();
            _inactivityCts?.Dispose();
            _inactivityCts = new CancellationTokenSource();
        }
        catch (ObjectDisposedException)
        {
            // Can happen during disconnection, safe to ignore.
        }
    }

    private async Task CloseSocketForReconnectAsync()
    {
        if (_webSocket != null)
        {
            try
            {
                // Close the output to signal the server, then wait briefly for the remote close frame.
                // This will cause ReceiveAsync in the ReceiveLoop to throw, triggering the reconnect logic there.
                await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Stale connection", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarningWithCaller($"Exception while closing stale socket for {SourceExchange}: {ex.Message}");
            }
        }
    }

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
            // clean up inactivity cancellation token
            _inactivityCts?.Cancel();
            _inactivityCts?.Dispose();
            _inactivityCts = null;

            // cleanup websocket connectivity
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

            var allTasks = new List<Task>();
            if (_receiveTask != null) allTasks.Add(_receiveTask);
            if (_heartbeatTask != null) allTasks.Add(_heartbeatTask);

            if (allTasks.Any())
            {
                Task.WhenAll(allTasks).Wait(TimeSpan.FromSeconds(2));
            }

            CleanupConnection();
        }

        _isDisposed = true;
    }

    #endregion
}
