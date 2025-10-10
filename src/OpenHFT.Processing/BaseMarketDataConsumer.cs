using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Models;
using OpenHFT.Processing.Interfaces;

namespace OpenHFT.Processing;

public abstract class BaseMarketDataConsumer : IMarketDataConsumer
{
    public abstract string ConsumerName { get; }
    public abstract IReadOnlyCollection<Instrument> Instruments { get; }
    public abstract ExchangeTopic Topic { get; }

    protected readonly ILogger _logger;
    private readonly BlockingCollection<MarketDataEvent> _eventQueue;
    private Task? _processingTask;
    private CancellationTokenSource? _cancellationTokenSource;

    protected BaseMarketDataConsumer(ILogger logger, int queueCapacity = 1024)
    {
        _logger = logger;
        // Bounded capacity acts as a back-pressure mechanism.
        _eventQueue = new BlockingCollection<MarketDataEvent>(queueCapacity);
    }

    public void Start()
    {
        _logger.LogInformationWithCaller($"Starting consumer: {ConsumerName}");
        _cancellationTokenSource = new CancellationTokenSource();
        _processingTask = Task.Factory.StartNew(
            () =>
            {
                Thread.CurrentThread.Name = $"Consumer: {ConsumerName}";
                ProcessEventQueue(_cancellationTokenSource.Token);
            },
            _cancellationTokenSource.Token,
            TaskCreationOptions.LongRunning, // Hint to the scheduler that this is a dedicated thread
            TaskScheduler.Default);
    }

    public void Stop()
    {
        _logger.LogInformationWithCaller($"Stopping consumer: {ConsumerName}");
        _eventQueue.CompleteAdding();
        _cancellationTokenSource?.Cancel();

        // Wait for the processing task to finish.
        _processingTask?.Wait(TimeSpan.FromSeconds(5));
    }

    public void Post(in MarketDataEvent data)
    {
        // Non-blocking. If the queue is full, it will drop the event.
        if (!_eventQueue.TryAdd(data))
        {
            _logger.LogWarning("{ConsumerName}'s event queue is full. Dropping event for InstrumentId {InstrumentId}.", ConsumerName, data.InstrumentId);
        }
    }

    private void ProcessEventQueue(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller($"{ConsumerName} processing thread started.");
        try
        {
            foreach (var marketEvent in _eventQueue.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    OnMarketData(marketEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in {ConsumerName} while processing event for InstrumentId {InstrumentId}.", ConsumerName, marketEvent.InstrumentId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // This is expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"{ConsumerName} processing thread terminated unexpectedly.");
        }
        _logger.LogInformationWithCaller($"{ConsumerName} processing thread stopped.");
    }

    /// <summary>
    /// Concrete consumer classes must implement this method to define their business logic.
    /// </summary>
    protected abstract void OnMarketData(in MarketDataEvent data);
}
