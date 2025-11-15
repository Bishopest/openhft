using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Gateway.Interfaces;

namespace OpenHFT.Service;

public class FeedOrchestrator : IHostedService
{
    private readonly ILogger<FeedOrchestrator> _logger;
    private readonly IEnumerable<IFeedAdapter> _feedAdapters;
    private readonly ITimeSyncManager _timeSyncManager;
    private readonly IConfiguration _configuration;
    private readonly ISubscriptionManager _subscriptionManager;

    public FeedOrchestrator(
        ILogger<FeedOrchestrator> logger,
        IFeedAdapterRegistry feedAdapterRegistry,
        IConfiguration configuration,
        ITimeSyncManager timeSyncManager,
        ISubscriptionManager subscriptionManager
        )
    {
        _logger = logger;
        _feedAdapters = feedAdapterRegistry.GetAllAdapters();
        _configuration = configuration;
        _timeSyncManager = timeSyncManager;
        _subscriptionManager = subscriptionManager;

        foreach (var adapter in _feedAdapters)
        {
            adapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;
        }
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
        _logger.LogInformationWithCaller($"Connecting to {adapter.SourceExchange}/{adapter.ProdType}...");
        await adapter.ConnectAsync(cancellationToken);
        _logger.LogInformationWithCaller($"Successfully connected to {adapter.SourceExchange}/{adapter.ProdType}.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Feed Orchestrator is stopping all adapters...");
        var shutdownTasks = _feedAdapters.Select(adapter => adapter.DisconnectAsync(cancellationToken));
        await Task.WhenAll(shutdownTasks);
        _timeSyncManager.Stop();
        _logger.LogInformationWithCaller("All feed adapters have been stopped.");
    }

    private void OnAdapterConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (e.IsConnected && sender is IFeedAdapter adapter)
        {
            _logger.LogInformationWithCaller($"Adapter {adapter.SourceExchange} reconnected. Attempting to re-authenticate.");
            _ = AuthenticateAdapterAsync(adapter);
        }
    }

    private async Task AuthenticateAdapterAsync(IFeedAdapter adapter)
    {
        // StartAdapterAsync의 인증 로직과 동일
        if (adapter is BaseAuthFeedAdapter authAdapter)
        {
            var apiKey = _configuration[$"{adapter.SourceExchange.ToString().ToUpper()}_{adapter.ExecMode.ToString().ToUpper()}_API_KEY"];
            var apiSecret = _configuration[$"{adapter.SourceExchange.ToString().ToUpper()}_{adapter.ExecMode.ToString().ToUpper()}_API_SECRET"];
            if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret))
            {
                try
                {
                    _logger.LogInformationWithCaller($"Re-authenticating with {adapter.SourceExchange}...");
                    await authAdapter.AuthenticateAsync(apiKey, apiSecret);
                }
                catch (Exception ex)
                {
                    _logger.LogErrorWithCaller(ex, $"Failed to re-authenticate with {adapter.SourceExchange}.");
                }
            }
        }
    }
}
