using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Processing;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting;

public class QuotingInstanceManager : IQuotingInstanceManager
{
    private readonly ILogger<QuotingInstanceManager> _logger;
    private readonly IQuotingInstanceFactory _factory;
    private readonly ConcurrentDictionary<int, QuotingInstance> _activeInstances = new();
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly MarketDataManager _marketDataManager;

    public QuotingInstanceManager(
        ILogger<QuotingInstanceManager> logger,
        IQuotingInstanceFactory factory,
        IInstrumentRepository instrumentRepository,
        MarketDataManager marketDataManager
        )
    {
        _logger = logger;
        _factory = factory;
        _instrumentRepository = instrumentRepository;
        _marketDataManager = marketDataManager;
    }

    // --- IQuotingInstanceManager Implementation ---
    public bool DeployStrategy(QuotingParameters parameters)
    {
        var instrumentId = parameters.InstrumentId;
        if (_activeInstances.ContainsKey(instrumentId))
        {
            _logger.LogWarningWithCaller($"Quoting strategy already deployed for instrument ID {instrumentId}. Retiring it before deploying the new one.");
            RetireStrategy(instrumentId);
            return false;
        }

        var instrument = _instrumentRepository.GetById(instrumentId);
        if (instrument == null)
        {
            _logger.LogWarningWithCaller($"Cannot deploy quoting strategy. Instrument ID {instrumentId} not found.");
            return false;
        }

        try
        {
            _logger.LogInformationWithCaller($"Deploying new quoting strategy for instrument ID {instrumentId}.");
            var instance = _factory.Create(parameters);

            if (_activeInstances.TryAdd(instrumentId, instance))
            {
                instance.Start(_marketDataManager);
                _logger.LogInformationWithCaller($"Successfully deployed quoting strategy for instrument {instrument.Symbol}.");
                return true;
            }
            else
            {
                _logger.LogWarningWithCaller($"Failed to add quoting strategy instance to active instances for instrument ID {instrumentId}.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error deploying quoting strategy for instrument {instrument.Symbol}.");
            return false;
        }
    }

    public IReadOnlyCollection<QuotingInstance> GetAllInstances()
    {
        return _activeInstances.Values.ToList().AsReadOnly();
    }

    public QuotingInstance? GetInstance(int instrumentId)
    {
        _activeInstances.TryGetValue(instrumentId, out var instance);
        return instance;
    }

    public bool RetireStrategy(int instrumentId)
    {
        if (!_activeInstances.TryRemove(instrumentId, out var instance))
        {
            _logger.LogWarningWithCaller($"No active quoting strategy found for instrument ID {instrumentId} to retire.");
            return false;
        }

        instance.Stop(_marketDataManager);
        _logger.LogInformationWithCaller($"Successfully retired quoting strategy for instrument ID {instrumentId}.");
        return true;
    }
}