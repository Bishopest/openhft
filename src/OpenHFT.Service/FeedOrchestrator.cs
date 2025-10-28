using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Gateway.Interfaces;

namespace OpenHFT.Service;

public class FeedOrchestrator : IHostedService
{
    private readonly ILogger<FeedOrchestrator> _logger;
    private readonly IEnumerable<IFeedAdapter> _feedAdapters;
    private readonly ITimeSyncManager _timeSyncManager;

    public FeedOrchestrator(
        ILogger<FeedOrchestrator> logger,
        IFeedAdapterRegistry feedAdapterRegistry,
        ITimeSyncManager timeSyncManager
        )
    {
        _logger = logger;
        _feedAdapters = feedAdapterRegistry.GetAllAdapters();
        _timeSyncManager = timeSyncManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Feed Orchestrator is starting all adapters...");
        var startupTasks = _feedAdapters.Select(adapter => StartAdapterAsync(adapter, cancellationToken));
        await Task.WhenAll(startupTasks);
        await _timeSyncManager.StartAsync(cancellationToken);
        _logger.LogInformationWithCaller("All feed adapters have been started.");
    }

    private async Task StartAdapterAsync(IFeedAdapter adapter, CancellationToken cancellationToken)
    {
        await adapter.ConnectAsync(cancellationToken);
        // await adapter.AuthenticateAsync("YOUR_API_KEY", "YOUR_API_SECRET", cancellationToken);

        if (adapter.IsConnected)
        {
            _logger.LogInformationWithCaller($"Adapter {adapter.SourceExchange}-{adapter.ProdType} connected successfully.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Feed Orchestrator is stopping all adapters...");
        var shutdownTasks = _feedAdapters.Select(adapter => adapter.DisconnectAsync(cancellationToken));
        await Task.WhenAll(shutdownTasks);
        _timeSyncManager.Stop();
        _logger.LogInformationWithCaller("All feed adapters have been stopped.");
    }
}
