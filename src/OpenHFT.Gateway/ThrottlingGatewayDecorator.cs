using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Api;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Core.Utils;

namespace OpenHFT.Gateway;


/// <summary>
/// A decorator for an IOrderGateway that enforces rate limits and prioritizes the latest modification requests.
/// </summary>
public sealed class ThrottlingGatewayDecorator : IOrderGateway, IDisposable
{
    private record PendingRequestWrapper(object Request, object Tcs);

    private readonly IOrderGateway _wrappedGateway;
    private readonly ILogger<ThrottlingGatewayDecorator> _logger;
    private readonly RateLimiter _perSecondLimiter;
    private readonly RateLimiter _perMinuteLimiter;

    // For NewOrderRequests, which should not be overwritten.
    private readonly ConcurrentQueue<PendingRequestWrapper> _newOrderQueue = new();

    // For Replace/Cancel requests, keyed by ExchangeOrderId. This ensures only the latest is kept.
    private readonly ConcurrentDictionary<string, PendingRequestWrapper> _modificationRequests = new();

    private readonly CancellationTokenSource _cts;
    private readonly Task _processingTask;

    public ExchangeEnum SourceExchange => _wrappedGateway.SourceExchange;
    public ProductType ProdType => _wrappedGateway.ProdType;

    public ThrottlingGatewayDecorator(
        IOrderGateway wrappedGateway,
        ILogger<ThrottlingGatewayDecorator> logger,
        RateLimiterConfig perSecondConfig, // e.g., 300 requests, 1 second
        RateLimiterConfig perMinuteConfig  // e.g., 1500 requests, 1 minute
    )
    {
        _wrappedGateway = wrappedGateway;
        _logger = logger;
        _perSecondLimiter = new RateLimiter(perSecondConfig);
        _perMinuteLimiter = new RateLimiter(perMinuteConfig);
        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessRequestsAsync(_cts.Token));
    }

    public Task<OrderPlacementResult> SendNewOrderAsync(NewOrderRequest request, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<OrderPlacementResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new PendingRequestWrapper(request, tcs);
        _newOrderQueue.Enqueue(wrapper);
        return tcs.Task;
    }

    public Task<OrderModificationResult> SendReplaceOrderAsync(ReplaceOrderRequest request, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<OrderModificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new PendingRequestWrapper(request, tcs);
        // AddOrUpdate will insert or overwrite the existing request for this ExchangeOrderId.
        _modificationRequests[request.OrderId] = wrapper;
        return tcs.Task;
    }

    public Task<OrderModificationResult> SendCancelOrderAsync(CancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<OrderModificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newWrapper = new PendingRequestWrapper(request, tcs);
        _modificationRequests.AddOrUpdate(
            request.OrderId,
            addValueFactory: (key) => newWrapper,
            updateValueFactory: (key, oldWrapper) =>
            {
                if (oldWrapper.Tcs is TaskCompletionSource<OrderModificationResult> oldTcs)
                {
                    _logger.LogWarningWithCaller($"Superseding a pending '{oldWrapper.Request.GetType().Name}' with a new Cancel request for OrderId {key}.");
                    oldTcs.TrySetCanceled();
                }
                return newWrapper;
            }
        );

        return tcs.Task;
    }

    private async Task ProcessRequestsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller($"Request processing task started for {SourceExchange}.");
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_perSecondLimiter.TryAcquireToken() && _perMinuteLimiter.TryAcquireToken())
            {
                object? requestToSend = null;
                object? tcs = null;

                // Prioritize modifications (replace/cancel) over new orders.
                if (!_modificationRequests.IsEmpty)
                {
                    // Attempt to grab a modification request. This is not atomic but is acceptable.
                    var key = _modificationRequests.Keys.FirstOrDefault();
                    if (key != null && _modificationRequests.TryRemove(key, out var modWrapper))
                    {
                        requestToSend = modWrapper.Request;
                        tcs = modWrapper.Tcs;
                    }
                }
                else if (_newOrderQueue.TryDequeue(out var newOrderWrapper))
                {
                    requestToSend = newOrderWrapper.Request;
                    tcs = newOrderWrapper.Tcs;
                }

                if (requestToSend != null && tcs != null)
                {
                    // Fire-and-forget the execution to avoid blocking the processing loop.
                    _ = ExecuteRequestAsync(requestToSend, tcs);
                }
                else
                {
                    // No work to do, yield to prevent a tight loop.
                    await Task.Delay(1, cancellationToken);
                }
            }
            else
            {
                // Rate limit exceeded, wait briefly before retrying.
                await Task.Delay(5, cancellationToken);
            }
        }
        _logger.LogWarningWithCaller($"Request processing task stopped for {SourceExchange}.");
    }

    private async Task ExecuteRequestAsync(object request, object tcs)
    {
        try
        {
            switch (request)
            {
                case NewOrderRequest newReq:
                    var placementTcs = (TaskCompletionSource<OrderPlacementResult>)tcs;
                    var placementResult = await _wrappedGateway.SendNewOrderAsync(newReq);
                    placementTcs.SetResult(placementResult);
                    break;
                case ReplaceOrderRequest replaceReq:
                    var replaceTcs = (TaskCompletionSource<OrderModificationResult>)tcs;
                    var replaceResult = await _wrappedGateway.SendReplaceOrderAsync(replaceReq);
                    replaceTcs.SetResult(replaceResult);
                    break;
                case CancelOrderRequest cancelReq:
                    var cancelTcs = (TaskCompletionSource<OrderModificationResult>)tcs;
                    var cancelResult = await _wrappedGateway.SendCancelOrderAsync(cancelReq);
                    cancelTcs.SetResult(cancelResult);
                    break;
                default:
                    _logger.LogWarningWithCaller($"Unknown request type in processing queue: {request.GetType().Name}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Failed to execute request of type {request.GetType().Name}.");
            // Propagate the exception to the original caller.
            if (tcs.GetType().GetMethod("TrySetException") is { } method)
            {
                method.Invoke(tcs, new object[] { ex });
            }
        }
    }

    // --- Other IOrderGateway methods can be simple pass-throughs or throttled as well ---
    public Task<RestApiResult<OrderStatusReport>> FetchOrderStatusAsync(string exchangeOrderId, CancellationToken cancellationToken = default)
    {
        // For simplicity, this is a direct pass-through, but it could also be throttled.
        return _wrappedGateway.FetchOrderStatusAsync(exchangeOrderId, cancellationToken);
    }

    public Task CancelAllOrdersAsync(string symbol, CancellationToken cancellationToken = default)
    {
        return _wrappedGateway.CancelAllOrdersAsync(symbol, cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            // Signal the cancellation to the processing task.
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            // Wait for the task to acknowledge the cancellation and terminate.
            // This will throw if the task ends in a Canceled state, which is expected.
            _processingTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            // This is the expected and normal outcome. The task was successfully canceled.
            _logger.LogDebug("Processing task canceled gracefully as part of Dispose.");
        }
        catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Also handle the case where Wait() wraps the OperationCanceledException in an AggregateException.
            _logger.LogDebug("Processing task canceled gracefully (caught as AggregateException).");
        }
        finally
        {
            // Ensure CancellationTokenSource is always disposed.
            _cts.Dispose();
            _processingTask.Dispose(); // It's good practice to dispose the task as well.
        }
    }
}