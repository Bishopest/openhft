using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Service;

public class SubscriptionInitializationService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionInitializationService> _logger;

    public SubscriptionInitializationService(IServiceProvider serviceProvider, ILogger<SubscriptionInitializationService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(2000, cancellationToken);

        _logger.LogInformationWithCaller("Initializing market data subscriptions...");
        // ISubscriptionManager를 스코프에서 직접 해결하여 사용
        using (var scope = _serviceProvider.CreateScope())
        {
            var subscriptionManager = scope.ServiceProvider.GetRequiredService<ISubscriptionManager>();
            await subscriptionManager.InitializeSubscriptionsAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}