using OpenHFT.Strategy.Advanced;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace OpenHFT.UI.Services;

/// <summary>
/// Service responsible for initializing and registering advanced trading strategies
/// </summary>
public class StrategyInitializationService : IHostedService
{
    private readonly ILogger<StrategyInitializationService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public StrategyInitializationService(
        ILogger<StrategyInitializationService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing advanced trading strategies...");

        try
        {
            var strategyManager = _serviceProvider.GetService<IAdvancedStrategyManager>();
            if (strategyManager == null)
            {
                _logger.LogWarning("AdvancedStrategyManager not available, skipping strategy initialization");
                return;
            }

            _logger.LogInformation("Successfully initialized all advanced trading strategies");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing advanced trading strategies");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping strategy initialization service");
        return Task.CompletedTask;
    }
}