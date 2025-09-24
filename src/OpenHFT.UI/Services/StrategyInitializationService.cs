using OpenHFT.Strategy.Advanced;
using OpenHFT.Strategy.Advanced.Arbitrage;
using OpenHFT.Strategy.Advanced.MarketMaking;
using OpenHFT.Strategy.Advanced.Momentum;
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

            // Register Triangular Arbitrage Strategy
            await RegisterTriangularArbitrageStrategy(strategyManager, cancellationToken);

            // Register Optimal Market Making Strategy
            await RegisterOptimalMarketMakingStrategy(strategyManager, cancellationToken);

            // Register ML Momentum Strategy
            await RegisterMLMomentumStrategy(strategyManager, cancellationToken);

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

    private async Task RegisterTriangularArbitrageStrategy(IAdvancedStrategyManager strategyManager, CancellationToken cancellationToken)
    {
        try
        {
            var strategy = new TriangularArbitrageStrategy(
                _serviceProvider.GetRequiredService<ILogger<TriangularArbitrageStrategy>>());

            var allocation = new StrategyAllocation
            {
                StrategyName = "TriangularArbitrage",
                CapitalAllocation = 10000m, // $10,000 allocation
                MaxPosition = 1000m,
                RiskLimit = 0.02m, // 2% risk limit
                IsEnabled = false
            };

            await strategyManager.RegisterStrategy(strategy, allocation);
            _logger.LogInformation("Registered Triangular Arbitrage strategy");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering Triangular Arbitrage strategy");
        }
    }

    private async Task RegisterOptimalMarketMakingStrategy(IAdvancedStrategyManager strategyManager, CancellationToken cancellationToken)
    {
        try
        {
            var strategy = new OptimalMarketMakingStrategy(
                _serviceProvider.GetRequiredService<ILogger<OptimalMarketMakingStrategy>>());

            var allocation = new StrategyAllocation
            {
                StrategyName = "OptimalMarketMaking",
                CapitalAllocation = 15000m, // $15,000 allocation
                MaxPosition = 2000m,
                RiskLimit = 0.03m, // 3% risk limit
                IsEnabled = false
            };

            await strategyManager.RegisterStrategy(strategy, allocation);
            _logger.LogInformation("Registered Optimal Market Making strategy");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering Optimal Market Making strategy");
        }
    }

    private async Task RegisterMLMomentumStrategy(IAdvancedStrategyManager strategyManager, CancellationToken cancellationToken)
    {
        try
        {
            var strategy = new MLMomentumStrategy(
                _serviceProvider.GetRequiredService<ILogger<MLMomentumStrategy>>());

            var allocation = new StrategyAllocation
            {
                StrategyName = "MLMomentum",
                CapitalAllocation = 20000m, // $20,000 allocation
                MaxPosition = 1500m,
                RiskLimit = 0.025m, // 2.5% risk limit
                IsEnabled = false
            };

            await strategyManager.RegisterStrategy(strategy, allocation);
            _logger.LogInformation("Registered ML Momentum strategy");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering ML Momentum strategy");
        }
    }
}
