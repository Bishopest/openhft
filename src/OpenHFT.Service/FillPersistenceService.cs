using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Service;

public class FillPersistenceService : IHostedService
{
    private readonly ILogger<FillPersistenceService> _logger;
    private readonly IOrderRouter _orderRouter;
    private readonly IFillRepository _fillRepository;

    public FillPersistenceService(
        ILogger<FillPersistenceService> logger,
        IOrderRouter orderRouter,
        IFillRepository fillRepository)
    {
        _logger = logger;
        _orderRouter = orderRouter;
        _fillRepository = fillRepository;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller($"Fill Persistence Service is starting to write to {_fillRepository.GetDBPath()} and subscribing to OrderFilled events.");
        _orderRouter.OrderFilled += OnOrderFilled;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Fill Persistence Service is stopping.");
        _orderRouter.OrderFilled -= OnOrderFilled;
        return Task.CompletedTask;
    }

    private void OnOrderFilled(object? sender, Fill fill)
    {
        _logger.LogInformationWithCaller($"Persisting new fill: {fill.ToString()}");
        // Fire-and-forget: DB 쓰기가 다른 작업을 막지 않도록 함
        _ = _fillRepository.AddFillAsync(fill);
    }

}
