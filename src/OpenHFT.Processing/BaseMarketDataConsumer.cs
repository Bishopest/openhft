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
    protected readonly ExchangeTopic _topic;
    protected readonly ILogger _logger;
    public ExchangeTopic Topic => _topic;


    protected BaseMarketDataConsumer(ILogger logger, ExchangeTopic topic)
    {
        _logger = logger;
        _topic = topic;
    }

    public void Start()
    {
        _logger.LogInformationWithCaller($"Starting consumer: {ConsumerName}");
    }

    public void Stop()
    {
        _logger.LogInformationWithCaller($"Stopping consumer: {ConsumerName}");
    }

    /// <summary>
    /// This method is now called DIRECTLY by the MarketDataDistributor
    /// on the Disruptor's consumer thread.
    /// </summary>
    public void Post(in MarketDataEvent data)
    {
        try
        {
            // The logic from the old ProcessEventQueue is now here.
            OnMarketData(data);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error in {ConsumerName} while processing event for InstrumentId {data.InstrumentId}.");
        }
    }

    /// <summary>
    /// Concrete consumer classes must implement this method to define their business logic.
    /// </summary>
    protected abstract void OnMarketData(in MarketDataEvent data);
}
