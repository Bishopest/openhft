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
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

public class QuotingInstanceManager : IQuotingInstanceManager
{
    private readonly ILogger<QuotingInstanceManager> _logger;
    private readonly IQuotingInstanceFactory _factory;
    private readonly ConcurrentDictionary<int, QuotingInstance> _activeInstances = new();
    private readonly IInstrumentRepository _instrumentRepository;
    public event EventHandler<QuotePair> InstanceQuotePairCalculated;

    public QuotingInstanceManager(
        ILogger<QuotingInstanceManager> logger,
        IQuotingInstanceFactory factory,
        IInstrumentRepository instrumentRepository
        )
    {
        _logger = logger;
        _factory = factory;
        _instrumentRepository = instrumentRepository;
    }

    // --- IQuotingInstanceManager Implementation ---
    private QuotingInstance? DeployInstance(QuotingParameters parameters)
    {
        var instrumentId = parameters.InstrumentId;
        if (_activeInstances.ContainsKey(instrumentId))
        {
            _logger.LogWarningWithCaller($"Quoting instance already deployed for instrument ID {instrumentId}. Retiring it before deploying the new one.");
            RetireInstance(instrumentId);
            return null;
        }

        var instrument = _instrumentRepository.GetById(instrumentId);
        if (instrument == null)
        {
            _logger.LogWarningWithCaller($"Cannot deploy quoting instance. Instrument ID {instrumentId} not found.");
            return null;
        }

        try
        {
            _logger.LogInformationWithCaller($"Deploying new quoting instance for instrument ID {instrumentId}.");
            var instance = _factory.Create(parameters);
            if (instance == null)
            {
                _logger.LogWarningWithCaller($"Failed to create quoting instance instance for instrument ID {instrumentId}.");
                return null;
            }


            if (_activeInstances.TryAdd(instrumentId, instance))
            {
                instance.Engine.QuotePairCalculated += OnEngineQuotePairCalculated;
                instance.Start();
                _logger.LogInformationWithCaller($"Successfully deployed quoting instance for instrument {instrument.Symbol}.");
                return instance;
            }
            else
            {
                _logger.LogWarningWithCaller($"Failed to add quoting instance instance to active instances for instrument ID {instrumentId}.");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error deploying quoting instance for instrument {instrument.Symbol}.");
            return null;
        }
    }

    public QuotingInstance? UpdateInstanceParameters(QuotingParameters newParameters)
    {
        var instrumentId = newParameters.InstrumentId;
        if (!_activeInstances.TryGetValue(instrumentId, out var instance))
        {
            _logger.LogInformationWithCaller($"No instance found for {instrumentId}. Deploying and activating new instance.");
            var newInstance = DeployInstance(newParameters); // 새 인스턴스 생성 (비활성 상태)
            return newInstance;
        }

        // Get the current parameters from the engine.
        // This requires QuotingEngine to expose its current parameters.
        var engine = instance.Engine;
        var currentParameters = engine.CurrentParameters;

        // Check if core, immutable parameters have changed.
        if (newParameters.FvModel != currentParameters.FvModel ||
            newParameters.FairValueSourceInstrumentId != currentParameters.FairValueSourceInstrumentId ||
            newParameters.Type != currentParameters.Type)
        {
            _logger.LogInformationWithCaller($"Core parameters changed for InstrumentId {instrumentId}. Redeploying instance.");
            DestroyInstance(instrumentId);
            var newInstance = DeployInstance(newParameters);
            return newInstance;
        }

        _logger.LogInformationWithCaller($"Tunable parameters changed for InstrumentId {instrumentId}. Updating in-place.");
        // Only tunable parameters changed, so update the existing engine.
        engine.UpdateParameters(newParameters);
        engine.Activate();
        return instance;
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

    public bool RetireInstance(int instrumentId)
    {
        if (!_activeInstances.TryGetValue(instrumentId, out var instance))
        {
            _logger.LogWarningWithCaller($"No active quoting strategy found for instrument ID {instrumentId} to retire.");
            return false;
        }

        instance.Deactivate();
        _logger.LogInformationWithCaller($"Successfully deactivate quoting strategy for instrument ID {instrumentId}.");
        return true;
    }

    private QuotingInstance? DestroyInstance(int instrumentId)
    {
        if (_activeInstances.TryRemove(instrumentId, out var instance))
        {
            instance.Engine.QuotePairCalculated -= OnEngineQuotePairCalculated;
            instance.Stop();
            return instance;
        }
        return null;
    }

    /// <summary>
    /// Relays the event from a specific engine to the manager's public event.
    /// </summary>
    private void OnEngineQuotePairCalculated(object? sender, QuotePair e)
    {
        InstanceQuotePairCalculated?.Invoke(sender, e);
    }
}