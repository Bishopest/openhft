using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Exceptions;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;

namespace OpenHFT.Feed.Adapters;

public abstract class BaseFeedAdapter : IFeedAdapter
{
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private long _lastMessageTimestamp;
    // TCS to wait pong messages
    private TaskCompletionSource<bool> _pongTcs;
    // 0 = not reconnecting, 1 = reconnecting.
    private volatile int _reconnectionInProgress;

    protected readonly ILogger _logger;
    protected readonly ExecutionMode _executionMode;
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
    private readonly Dictionary<Instrument, HashSet<int>> _subscriptions = new();

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<FeedErrorEventArgs>? Error;
    public event EventHandler<MarketDataEvent>? MarketDataReceived;
    public event EventHandler<OrderStatusReport>? OrderUpdateReceived;

    public ProductType ProdType { get; }

    protected BaseFeedAdapter(ILogger logger, ProductType type, IInstrumentRepository instrumentRepository, ExecutionMode executionMode)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instrumentRepository = instrumentRepository;
        ProdType = type;
        _executionMode = executionMode;
    }

    #region Public Methods (IFeedAdapter Implementation)
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().Name);
        if (IsConnected) return;

        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

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
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                combinedCts.CancelAfter(TimeSpan.FromSeconds(5));
                await Task.WhenAll(tasksToWait).WaitAsync(combinedCts.Token).ConfigureAwait(false);
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

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        _logger.LogInformationWithCaller($"Successfully disconnected from {SourceExchange} WebSocket.");
    }

    public async Task SubscribeAsync(IEnumerable<Instrument> instruments, IEnumerable<ExchangeTopic> topics, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Cannot subscribe when not connected.");

        var subscriptionsToAdd = new Dictionary<Instrument, List<ExchangeTopic>>();
        var topicList = topics.ToList();
        lock (_subscriptionLock)
        {
            foreach (var instrument in instruments)
            {
                if (!_subscriptions.TryGetValue(instrument, out var subscribedTopics))
                {
                    subscribedTopics = new HashSet<int>();
                    _subscriptions[instrument] = subscribedTopics;
                }

                foreach (var topic in topicList)
                {
                    if (subscribedTopics.Add(topic.TopicId))
                    {
                        if (!subscriptionsToAdd.ContainsKey(instrument))
                        {
                            subscriptionsToAdd[instrument] = new List<ExchangeTopic>();
                        }
                        subscriptionsToAdd[instrument].Add(topic);
                    }
                }
            }
        }

        if (subscriptionsToAdd.Any())
        {
            _logger.LogInformationWithCaller($"Subscribing to {subscriptionsToAdd.Values.Sum(v => v.Count)} new topics across {subscriptionsToAdd.Count} instruments on {SourceExchange}.");
            await DoSubscribeAsync(subscriptionsToAdd, cancellationToken);
        }
    }

    public async Task UnsubscribeAsync(IEnumerable<Instrument> instruments, IEnumerable<ExchangeTopic> topics, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Cannot unsubscribe when not connected.");

        var subscriptionsToRemove = new Dictionary<Instrument, List<ExchangeTopic>>();
        var topicList = topics.ToList();
        lock (_subscriptionLock)
        {
            foreach (var instrument in instruments)
            {
                if (!_subscriptions.TryGetValue(instrument, out var subscribedTopics))
                {
                    continue;
                }

                foreach (var topic in topicList)
                {
                    if (subscribedTopics.Remove(topic.TopicId))
                    {
                        if (!subscriptionsToRemove.ContainsKey(instrument))
                        {
                            subscriptionsToRemove[instrument] = new List<ExchangeTopic>();
                        }
                        subscriptionsToRemove[instrument].Add(topic);
                    }
                }
            }
        }

        if (subscriptionsToRemove.Any())
        {
            _logger.LogInformationWithCaller($"Unsubscribing to {subscriptionsToRemove.Values.Sum(v => v.Count)} new topics across {subscriptionsToRemove.Count} instruments on {SourceExchange}.");
            await DoUnsubscribeAsync(subscriptionsToRemove, cancellationToken);
        }
    }

    public virtual Task AuthenticateAsync(string apiKey, string apiSecret, CancellationToken cancellationToken = default)
    {
        // By default, adapters do not support authentication.
        // Derived classes that require it must override this method.
        _logger.LogWarningWithCaller($"{GetType().Name} does not support authentication.");
        return Task.FromException(new NotSupportedException($"{GetType().Name} does not support authentication."));
    }
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Connection and Receive Logic

    private async Task InitiateReconnectAsync(string reason)
    {
        if (Interlocked.CompareExchange(ref _reconnectionInProgress, 1, 0) == 0)
        {
            _logger.LogWarningWithCaller($"Initiating {SourceExchange} reconnection due to: {reason}");
            OnConnectionStateChanged(false, "Connection Lost");

            // 1. Capture the tasks from the old session before they are cleared.
            var tasksToWait = new List<Task>();
            if (_receiveTask != null) tasksToWait.Add(_receiveTask);
            if (_heartbeatTask != null) tasksToWait.Add(_heartbeatTask);

            // 2. Signal the old session to terminate by cleaning up its resources.
            CleanupConnection();

            // 3. Explicitly wait for the old tasks to finish.
            if (tasksToWait.Any())
            {
                try
                {
                    // Use a short timeout as a safeguard against stuck tasks.
                    await Task.WhenAll(tasksToWait).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Exceptions (like ObjectDisposedException or TimeoutException) are expected here
                    // as the tasks are being forcefully terminated. We can log them for debugging.
                    _logger.LogTrace(ex, "Caught expected exception while waiting for old tasks to terminate during reconnect.");
                }
            }

            // 4. Now that the slate is clean, start the new connection attempt.
            // The main _cancellationTokenSource is still valid.
            await ConnectWithRetryAsync(_cancellationTokenSource?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
    }
    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
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
                    Interlocked.Exchange(ref _reconnectionInProgress, 0);
                    _logger.LogInformationWithCaller($"Successfully connected to {SourceExchange} WebSocket.");

                    // Clear old subscriptions to allow SubscriptionManager to re-subscribe cleanly.
                    lock (_subscriptionLock)
                    {
                        _subscribedInsts.Clear();
                    }

                    OnConnectionStateChanged(true, "Connected Successfully");

                    // Start background tasks
                    Interlocked.Exchange(ref _lastMessageTimestamp, Stopwatch.GetTimestamp());
                    _receiveTask = Task.Run(() => ReceiveLoop(cancellationToken), cancellationToken);
                    _heartbeatTask = Task.Run(() => HeartbeatLoop(cancellationToken), cancellationToken);
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
            finally
            {
                Interlocked.Exchange(ref _reconnectionInProgress, 0);
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
            var reason = $"{SourceExchange} WebSocket connection closed unexpectedly. Reason: {ex.Message}";
            _logger.LogErrorWithCaller(ex, reason);
            _ = InitiateReconnectAsync(reason);
        }
        catch (Exception ex)
        {
            if (ex is ObjectDisposedException)
            {
                _logger.LogInformationWithCaller($"{SourceExchange} ReceiveLoop is stopping due to socket disposal during reconnect.");
                return;
            }

            _logger.LogErrorWithCaller(ex, $"Unhandled exception in {SourceExchange} ReceiveLoop.");
            OnError(new FeedErrorEventArgs(new FeedReceiveException(SourceExchange, "Unhandled exception in receive loop", ex), null));
        }
    }

    private async Task HeartbeatLoop(CancellationToken cancellationToken)
    {
        var inactivityTimeout = GetInactivityTimeout();
        var pingTimeout = GetPingTimeout();
        var checkInterval = TimeSpan.FromSeconds(5);

        if (IsHeartbeatEnabled)
        {
            _logger.LogInformationWithCaller($"Heartbeat/Monitor loop for {SourceExchange} started. Check interval: {checkInterval.TotalSeconds}s, Inactivity timeout: {inactivityTimeout.TotalSeconds}s.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(checkInterval, cancellationToken).ConfigureAwait(false);

                if (_webSocket is null || _webSocket.State != WebSocketState.Open)
                {
                    var reason = $"WebSocket state for {SourceExchange} is not Open (State: {_webSocket?.State.ToString() ?? "null"}).";
                    _ = InitiateReconnectAsync(reason);
                    return; // Exit this loop; a new one will be created upon successful reconnection.
                }

                if (!IsHeartbeatEnabled)
                {
                    // If heartbeats are disabled (e.g., Binance), this loop acts as a simple
                    // state poller. We just delay and then check the state again in the next iteration.
                    continue;
                }

                var lastMessageElapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastMessageTimestamp));
                if (lastMessageElapsed < inactivityTimeout)
                {
                    continue;
                }

                // If we reach here, no messages were received. Send a ping.
                _logger.LogInformationWithCaller($"No message received for {inactivityTimeout.TotalSeconds}s. Sending ping to {SourceExchange}.");

                _pongTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pingMessage = GetPingMessage();
                if (pingMessage != null)
                {
                    await SendMessageAsync(pingMessage, cancellationToken).ConfigureAwait(false);
                }

                // Wait for the pong response or a timeout.
                using var timeoutCts = new CancellationTokenSource(pingTimeout);
                var pongTask = _pongTcs.Task;
                var completedTask = await Task.WhenAny(pongTask, Task.Delay(pingTimeout, timeoutCts.Token)).ConfigureAwait(false);

                if (completedTask != pongTask || !pongTask.Result)
                {
                    // call the centralized reconnect method and then exit the loop.
                    var reason = $"Pong timeout from {SourceExchange} within {pingTimeout.TotalSeconds}s.";
                    _ = InitiateReconnectAsync(reason);
                    return; // Exit this loop.
                }

                _logger.LogInformationWithCaller($"Successfully received pong from {SourceExchange}.");
            }
            catch (OperationCanceledException ex)
            {
                // cancellationToken exception thrown from DisconnectAsync()
                break;
            }
            catch (ObjectDisposedException ex)
            {
                // This means another thread (likely ReceiveLoop) already initiated a reconnect.
                // Our job here is to simply exit this loop cleanly.
                _logger.LogInformationWithCaller($"HeartbeatLoop for {SourceExchange} stopping as socket was disposed by a reconnect process.");
                break;
            }
            catch (Exception ex)
            {
                var reason = $"An error occurred in the heartbeat/monitor loop for {SourceExchange}.";
                _logger.LogErrorWithCaller(ex, reason);
                _ = InitiateReconnectAsync(reason);
                return; // Exit this loop.
            }
        }
        _logger.LogInformationWithCaller($"Heartbeat/Monitor loop for {SourceExchange} has stopped.");
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
    protected abstract Task DoSubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken);

    /// <summary>
    /// Sends unsubscription messages to the WebSocket.
    /// </summary>
    protected abstract Task DoUnsubscribeAsync(IDictionary<Instrument, List<ExchangeTopic>> subscriptions, CancellationToken cancellationToken);

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
        Interlocked.Exchange(ref _lastMessageTimestamp, Stopwatch.GetTimestamp());
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

    protected virtual void OnOrderUpdateReceived(OrderStatusReport e)
    {
        OrderUpdateReceived?.Invoke(this, e);
    }

    private void CleanupConnection()
    {
        lock (_connectionLock)
        {
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

            _cancellationTokenSource?.Dispose();
        }

        _isDisposed = true;
    }

    #endregion
}
