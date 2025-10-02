using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Processing.Interfaces;

public interface IMarketDataConsumer
{
    string ConsumerName { get; }
    Instrument Instrument { get; }

    /// <summary>
    /// Posts a market data event to the consumer's internal queue for processing.
    /// This method should be non-blocking and return immediately.
    /// keyword 'in' => pass by reference & read-only
    /// </summary>
    void Post(in MarketDataEvent data);

    /// <summary>
    /// Starts the consumer's internal processing thread.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the consumer's internal processing thread and waits for it to finish.
    /// </summary>
    void Stop();
}