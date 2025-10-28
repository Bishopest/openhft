using System;
using Disruptor.Dsl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Service;

public class DisruptorService : IHostedService
{
    private readonly Disruptor<MarketDataEventWrapper> _marketDataDisruptor;
    private readonly Disruptor<OrderStatusReportWrapper> _orderUpdateDisruptor;
    private readonly ILogger<DisruptorService> _logger;

    public DisruptorService(ILogger<DisruptorService> logger, Disruptor<MarketDataEventWrapper> mdDisruptor, Disruptor<OrderStatusReportWrapper> ouDisruptor)
    {
        _logger = logger;
        _marketDataDisruptor = mdDisruptor;
        _orderUpdateDisruptor = ouDisruptor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _marketDataDisruptor.Start();
        _orderUpdateDisruptor.Start();
        _logger.LogInformationWithCaller("Disruptors started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _marketDataDisruptor.Shutdown();
        _orderUpdateDisruptor.Shutdown();
        _logger.LogInformationWithCaller("Disruptors stopped.");
        return Task.CompletedTask;
    }
}