using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Hedging;

public class HedgerManager : IHedgerManager
{
    private readonly ILogger<HedgerManager> _logger;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly IQuotingInstanceManager _quotingInstanceManager;
    private readonly IOrderFactory _orderFactory;
    private readonly IMarketDataManager _marketDataManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IFeedHandler _feedHandler;
    // Key: QuotingInstrumentId (Source)
    private readonly ConcurrentDictionary<int, Hedger> _hedgers = new();
    private readonly IFxRateService _fxRateManager;
    public event EventHandler<HedgingParameters>? HedgingParametersUpdated;

    public HedgerManager(
        ILogger<HedgerManager> logger,
        ILoggerFactory loggerFactory,
        IInstrumentRepository instrumentRepository,
        IQuotingInstanceManager quotingInstanceManager,
        IOrderFactory orderFactory,
        IMarketDataManager marketDataManager,
        IFeedHandler feedHandler,
        IFxRateService fxRateManager
        )
    {
        _logger = logger;
        _instrumentRepository = instrumentRepository;
        _quotingInstanceManager = quotingInstanceManager;
        _orderFactory = orderFactory;
        _marketDataManager = marketDataManager;
        _loggerFactory = loggerFactory;
        _feedHandler = feedHandler;
        _fxRateManager = fxRateManager;

        _quotingInstanceManager.GlobalOrderFilled += OnGlobalOrderFilled;
        _feedHandler.AdapterConnectionStateChanged += OnAdapterConnectionStateChanged;
    }

    public bool UpdateHedgingParameters(HedgingParameters parameters)
    {
        // 1. 유효성 검사
        var quoteInstrument = _instrumentRepository.GetById(parameters.QuotingInstrumentId);
        var hedgeInstrument = _instrumentRepository.GetById(parameters.InstrumentId);

        if (quoteInstrument == null || hedgeInstrument == null)
        {
            _logger.LogWarningWithCaller($"Invalid instrument IDs. Quote: {parameters.QuotingInstrumentId}, Hedge: {parameters.InstrumentId}");
            return false;
        }

        // 2. 기존 Hedger 확인 및 중지/교체 로직
        if (_hedgers.TryGetValue(parameters.QuotingInstrumentId, out var existingHedger))
        {
            if (existingHedger.HedgeParameters == parameters)
            {
                return true;
            }

            _logger.LogInformationWithCaller($"Updating hedger for {quoteInstrument.Symbol} -> {hedgeInstrument.Symbol} , params => {parameters}");
            existingHedger.Deactivate();
        }

        var quotingInstance = _quotingInstanceManager.GetInstance(parameters.QuotingInstrumentId);
        if (quotingInstance == null)
        {
            _logger.LogWarningWithCaller($"Hedger can not start because quoting instance not found for {quoteInstrument.Symbol}");
            return false;
        }

        // 3. 새 Hedger 생성 및 시작
        var newHedger = new Hedger(
            _loggerFactory.CreateLogger<Hedger>(),
            quoteInstrument,
            hedgeInstrument,
            parameters,
            _orderFactory,
            quotingInstance.CurrentParameters.BookName,
            _marketDataManager,
            _fxRateManager
        );

        _hedgers[parameters.QuotingInstrumentId] = newHedger;
        newHedger.Activate();

        // 4. 상태 변경 이벤트 전송
        HedgingParametersUpdated?.Invoke(this, parameters);
        return true;
    }

    public bool StopHedging(int quotingInstrumentId)
    {
        if (_hedgers.TryGetValue(quotingInstrumentId, out var hedger))
        {
            hedger.Deactivate();
            _logger.LogInformationWithCaller($"Stopped hedging for QuoteInstrumentId {quotingInstrumentId}");
            HedgingParametersUpdated?.Invoke(this, hedger.HedgeParameters);
            return _hedgers.TryRemove(quotingInstrumentId, out var removed);
        }

        _logger.LogWarningWithCaller($"No active hedger found for QuoteInstrumentId {quotingInstrumentId}");
        return false;
    }

    private void OnGlobalOrderFilled(object? sender, Fill fill)
    {
        // QuotingInstance에서 발생한 체결(Fill)을 해당 Hedger에게 전달
        if (_hedgers.TryGetValue(fill.InstrumentId, out var hedger))
        {
            hedger.UpdateQuotingFill(fill);
        }
    }

    private void OnAdapterConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (e.IsConnected)
        {
            var deactivatedHedgers = _hedgers.Values.Where(h => !h.IsActive);
            foreach (var hedger in deactivatedHedgers)
            {
                var hedgeInstrumentId = hedger.HedgeParameters.InstrumentId;
                var hedgeInstrument = _instrumentRepository.GetById(hedgeInstrumentId);
                if (hedgeInstrument is null)
                {
                    _logger.LogWarningWithCaller($"Hedge instrument({hedgeInstrumentId}) not found, skip activation from connection to exchange {e.SourceExchange}");
                    continue;
                }

                if (hedgeInstrument.SourceExchange == e.SourceExchange)
                {
                    hedger.Activate();
                    HedgingParametersUpdated?.Invoke(this, hedger.HedgeParameters);
                    _logger.LogWarningWithCaller($"Resume hedger on hedging instrument {hedgeInstrument.Symbol} after {e.SourceExchange} connected");
                }
            }

            return;
        }

        _logger.LogWarningWithCaller($"Connection lost for exchange {e.SourceExchange}. Stop all active hedger.");

        foreach (var hedger in _hedgers.Values.ToList())
        {
            var hedgeInstrumentId = hedger.HedgeParameters.InstrumentId;
            var hedgeInstrument = _instrumentRepository.GetById(hedgeInstrumentId);
            if (hedgeInstrument is null)
            {
                _logger.LogWarningWithCaller($"Hedge instrument({hedgeInstrumentId}) not found, skip deactivation from disconnection");
                continue;
            }

            if (hedgeInstrument.SourceExchange == e.SourceExchange)
            {
                _logger.LogWarningWithCaller($"Deactivate hedger on hedging instrument {hedgeInstrument.Symbol} after {e.SourceExchange} disconnected");
                hedger.Deactivate();
                HedgingParametersUpdated?.Invoke(this, hedger.HedgeParameters);
            }
        }
    }

    public Hedger? GetHedger(int quotingInstrumentId)
    {
        _hedgers.TryGetValue(quotingInstrumentId, out var hedger);
        return hedger;
    }

    public IReadOnlyCollection<Hedger> GetAllHedgers()
    {

        return _hedgers.Values.ToList().AsReadOnly();
    }

    private ILogger<Hedger> CreateLoggerForHedger()
    {
        // LoggerFactory 주입받아서 생성하는 것이 정석이나, 여기서는 간단히 처리
        var factory = LoggerFactory.Create(builder => builder.AddConsole());
        return factory.CreateLogger<Hedger>();
    }

    public void Dispose()
    {
        _quotingInstanceManager.GlobalOrderFilled -= OnGlobalOrderFilled;
        _feedHandler.AdapterConnectionStateChanged -= OnAdapterConnectionStateChanged;
        foreach (var hedger in _hedgers.Values)
        {
            hedger.Deactivate();
        }
        _hedgers.Clear();
    }
}
