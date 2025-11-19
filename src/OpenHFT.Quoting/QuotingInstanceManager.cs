using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Processing;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

public class QuotingInstanceManager : IQuotingInstanceManager, IDisposable
{
    private readonly ILogger<QuotingInstanceManager> _logger;
    private readonly IQuotingInstanceFactory _factory;
    private readonly ConcurrentDictionary<int, QuotingInstance> _activeInstances = new();
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly IFeedHandler _feedHandler;

    public event EventHandler<QuotePair>? InstanceQuotePairCalculated;
    public event EventHandler<QuotingParameters>? InstanceParametersUpdated;

    public QuotingInstanceManager(
        ILogger<QuotingInstanceManager> logger,
        IQuotingInstanceFactory factory,
        IInstrumentRepository instrumentRepository,
        IFeedHandler feedHandler
        )
    {
        _logger = logger;
        _factory = factory;
        _instrumentRepository = instrumentRepository;
        _feedHandler = feedHandler;

        _feedHandler.AdapterConnectionStateChanged += OnAdapterConnectionStateChanged;
    }

    private void OnAdapterConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (e.IsConnected)
        {
            return;
        }

        _logger.LogWarningWithCaller($"Connection lost for exchange {e.SourceExchange}. Retiring all related quoting instances.");

        foreach (var instance in _activeInstances.Values.ToList())
        {
            var instrument = _instrumentRepository.GetById(instance.InstrumentId);
            if (instrument != null && instrument.SourceExchange == e.SourceExchange)
            {
                RetireInstance(instance.InstrumentId);
            }
        }
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
                instance.Engine.ParametersUpdated += InstanceParametersUpdated;
                instance.Start();
                InstanceParametersUpdated?.Invoke(this, instance.CurrentParameters);
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
        var oldParam = engine.CurrentParameters;
        engine.UpdateParameters(newParameters);
        // Only when update request with equal param take place "twice", activate it
        if (oldParam.Equals(newParameters))
        {
            engine.Activate();
        }
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

    public QuotingInstance? RetireInstance(int instrumentId)
    {
        if (!_activeInstances.TryGetValue(instrumentId, out var instance))
        {
            _logger.LogWarningWithCaller($"No active quoting strategy found for instrument ID {instrumentId} to retire.");
            return null;
        }

        instance.Deactivate();
        InstanceParametersUpdated?.Invoke(this, instance.CurrentParameters);
        _logger.LogInformationWithCaller($"Successfully deactivate quoting strategy for instrument ID {instrumentId}.");
        return instance;
    }

    private QuotingInstance? DestroyInstance(int instrumentId)
    {
        if (_activeInstances.TryRemove(instrumentId, out var instance))
        {
            instance.Engine.QuotePairCalculated -= OnEngineQuotePairCalculated;
            instance.Engine.ParametersUpdated -= InstanceParametersUpdated;
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

    public void Dispose()
    {
        _logger.LogInformationWithCaller("Disposing QuotingInstanceManager.");
        _feedHandler.AdapterConnectionStateChanged -= OnAdapterConnectionStateChanged;

        // 애플리케이션 종료 시 모든 인스턴스를 완전히 정리
        foreach (var instrumentId in _activeInstances.Keys.ToList())
        {
            if (_activeInstances.TryRemove(instrumentId, out var instance))
            {
                instance.Stop();
            }
        }
    }
}