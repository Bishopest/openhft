using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Processing;

namespace OpenHFT.Service;

public class MarketDataInitializerService : IHostedService
{
    private readonly ILogger<MarketDataInitializerService> _logger;
    private readonly IMarketDataManager _marketDataManager;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly SubscriptionConfig _config;

    public MarketDataInitializerService(
        ILogger<MarketDataInitializerService> logger,
        IMarketDataManager marketDataManager,
        IInstrumentRepository instrumentRepository,
        SubscriptionConfig config)
    {
        _logger = logger;
        _marketDataManager = marketDataManager;
        _instrumentRepository = instrumentRepository;
        _config = config;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Starting MarketDataInitializerService...");
        try
        {
            foreach (var group in _config.Subscriptions)
            {
                if (!Enum.TryParse<ExchangeEnum>(group.Exchange, true, out var exchange) ||
                    !Enum.TryParse<ProductType>(group.ProductType, true, out var productType))
                {
                    _logger.LogWarningWithCaller($"Invalid subscription group in configuration: {group}");
                    continue;
                }

                foreach (var symbol in group.Symbols)
                {
                    var instrument = _instrumentRepository.FindBySymbol(symbol, productType, exchange);
                    if (instrument != null)
                    {
                        _logger.LogInformationWithCaller($"Subscribing to instrument: {instrument.Symbol} on {exchange} ({productType})");
                        _marketDataManager.Install(instrument);
                    }
                    else
                    {
                        _logger.LogWarningWithCaller($"Instrument not found for symbol: {symbol} on {exchange} ({productType})");
                    }
                }
            }

            _logger.LogInformationWithCaller("MarketDataInitializerService started successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error occurred while initializing market data install.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
