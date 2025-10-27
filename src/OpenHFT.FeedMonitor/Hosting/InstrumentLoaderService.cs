using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Utils;

namespace OpenHFT.FeedMonitor.Hosting;

public class InstrumentLoaderService : IHostedService
{
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InstrumentLoaderService> _logger;
    private readonly IHostApplicationLifetime _appLifetime;

    public InstrumentLoaderService(
        IInstrumentRepository instrumentRepository,
        IConfiguration configuration,
        ILogger<InstrumentLoaderService> logger,
        IHostApplicationLifetime appLifetime)
    {
        _instrumentRepository = instrumentRepository;
        _configuration = configuration;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Loading instruments from CSV specified in configuration...");
        try
        {
            var dataFolder = _configuration["dataFolder"];

            if (string.IsNullOrEmpty(dataFolder))
            {
                throw new InvalidOperationException("Configuration key 'dataFolder' is not set in config.json.");
            }

            var instrumentsCsvPath = Path.Combine(dataFolder, "instruments.csv");
            _instrumentRepository.LoadFromCsv(instrumentsCsvPath);
            _logger.LogInformationWithCaller($"Instruments loaded successfully from '{instrumentsCsvPath}'.");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Failed to load instruments file. The application cannot continue and will shut down.");
            _appLifetime.StopApplication();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}