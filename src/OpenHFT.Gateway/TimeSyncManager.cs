using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Gateway.Interfaces;

namespace OpenHFT.Gateway;

public class TimeSyncManager : ITimeSyncManager, IDisposable
{
    private readonly ILogger<TimeSyncManager> _logger;
    private readonly IRestApiClientRegistry _apiClientRegistry;
    private Timer? _timer;

    public TimeSyncManager(ILogger<TimeSyncManager> logger, IRestApiClientRegistry apiClientRegistry)
    {
        _logger = logger;
        _apiClientRegistry = apiClientRegistry;
    }

    /// <summary>
    /// Starts the time synchronization process.
    /// Performs an initial sync immediately and then sets up a periodic timer.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Time Sync Manager is starting...");
        await DoWork(cancellationToken);
        _timer = new Timer(async _ => await DoWork(CancellationToken.None), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    private async Task DoWork(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Performing time synchronization...");

        var allClients = _apiClientRegistry.GetAllClients();
        if (!allClients.Any())
        {
            _logger.LogWarningWithCaller("No REST API clients found in the registry to sync time with.");
            return;
        }

        foreach (var client in allClients)
        {
            try
            {
                var serverTime = await client.GetServerTimeAsync(cancellationToken);
                TimeSync.UpdateTimeOffset(client.SourceExchange, serverTime);
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCaller(ex, $"Failed to sync time with {client.SourceExchange}.");
            }
        }
    }

    public void Stop()
    {
        _logger.LogInformationWithCaller("Time Sync Manager is stopping.");
        _timer?.Change(Timeout.Infinite, 0);
    }

    public void Dispose() => _timer?.Dispose();
}
