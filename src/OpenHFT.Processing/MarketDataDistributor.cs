using OpenHFT.Core.Collections;
using OpenHFT.Core.Models;
using OpenHFT.Processing.Interfaces;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace OpenHFT.Processing;

public class MarketDataDistributor
{
    private readonly ChannelReader<MarketDataEvent> _inputReader;
    private readonly ILogger<MarketDataDistributor> _logger;
    private readonly Dictionary<int, HashSet<IMarketDataConsumer>> _symbolSubscriptions;
    private long _distributedEventCount;
    private Task? _distributionTask;

    public MarketDataDistributor(ChannelReader<MarketDataEvent> inputReader, ILogger<MarketDataDistributor> logger)
    {
        _inputReader = inputReader;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Market Data Distributor is starting.");

        _distributionTask = Task.Run(() => DistributionLoop(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Market Data Distributor is stopping.");
        if (_distributionTask != null)
        {
            await _distributionTask;
        }
    }

    private async Task DistributionLoop(CancellationToken cancellationToken)
    {
        Thread.CurrentThread.Name = "Market-Processor";

        try
        {
            await foreach (var marketEvent in _inputReader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref _distributedEventCount);

                if (_symbolSubscriptions.TryGetValue(marketEvent.SymbolId, out var subscribers))
                {
                    foreach (var subscriber in subscribers)
                    {
                        var consumer = subscriber;
                        // Fire-and-forget으로 각 Consumer 병렬 실행
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                consumer.OnMarketData(marketEvent);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Failed to call OnMarketData on symbol id({marketEvent.SymbolId}) from Binance adapter");
                            }
                        }, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Distribution loop was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred in the distribution loop.");
        }
    }

    // Subscription management
    public void Subscribe(IMarketDataConsumer consumer, int symbolId)
    {
        if (!_symbolSubscriptions.ContainsKey(symbolId))
        {
            _symbolSubscriptions[symbolId] = new HashSet<IMarketDataConsumer>();
        }

        _symbolSubscriptions[symbolId].Add(consumer);
        _logger.LogInformation("Consumer {Consumer} subscribed to symbol {SymbolId}",
            consumer.GetType().Name, symbolId);
    }

    public void Unsubscribe(IMarketDataConsumer consumer, int symbolId)
    {
        if (_symbolSubscriptions.TryGetValue(symbolId, out var subscribers))
        {
            subscribers.Remove(consumer);

            if (subscribers.Count == 0)
            {
                _symbolSubscriptions.Remove(symbolId);
            }
        }
    }

}
