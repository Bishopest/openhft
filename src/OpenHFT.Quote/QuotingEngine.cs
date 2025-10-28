using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Processing;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

public class QuotingEngine : IQuotingEngine
{
    private readonly ILogger _logger;
    private readonly MarketMaker _marketMaker;
    private readonly IFairValueProviderFactory _fairValueProviderFactory;
    private readonly object _lock = new();
    private IFairValueProvider? _fairValueProvider;
    private QuotingParameters? _parameters;

    public event EventHandler<QuotePair> QuotePairCalculated;

    public Instrument QuotingInstrument { get; }

    public QuotingEngine(
        ILogger logger,
        Instrument instrument,
        MarketMaker marketMaker,
        IFairValueProviderFactory fairValueProviderFactory)
    {
        _logger = logger;
        QuotingInstrument = instrument;
        _marketMaker = marketMaker;
        _fairValueProviderFactory = fairValueProviderFactory;
    }

    public void Start(MarketDataManager marketDataManager)
    {
        if (_parameters == null)
        {
            _logger.LogWarningWithCaller($"Please set quoting paramters first for {QuotingInstrument.Symbol}");
            return;
        }

        if (_fairValueProvider == null)
        {
            _logger.LogWarningWithCaller($"Please set fair value provider first for {QuotingInstrument.Symbol}");
            return;
        }

        _logger.LogInformationWithCaller($"Starting QuotingEngine for {QuotingInstrument.Symbol}.");
        _fairValueProvider.FairValueChanged += OnFairValueChanged;
        marketDataManager.SubscribeOrderBook(QuotingInstrument.InstrumentId, $"QuotingEngine_{QuotingInstrument.Symbol}_{_fairValueProvider?.Model}", OnOrderBookUpdate);
    }

    public void Stop(MarketDataManager marketDataManager)
    {
        _logger.LogInformationWithCaller($"Stopping QuotingEngine for {QuotingInstrument.Symbol}.");
        if (_fairValueProvider != null)
        {
            marketDataManager.UnsubscribeOrderBook(QuotingInstrument.InstrumentId, $"QuotingEngine_{QuotingInstrument.Symbol}_{_fairValueProvider.Model}");
            _fairValueProvider.FairValueChanged -= OnFairValueChanged;
        }
        _ = _marketMaker.CancelAllQuotesAsync(); // Fire and forget cancel
    }

    public void UpdateParameters(QuotingParameters newParameters)
    {
        if (newParameters.InstrumentId != QuotingInstrument.InstrumentId)
        {
            _logger.LogWarningWithCaller($"Invalid instrument id({newParameters.InstrumentId}), This is QuotingEngine for id({QuotingInstrument.InstrumentId}).");
            return;
        }

        lock (_lock)
        {
            if (newParameters == _parameters) return;

            if (_parameters.HasValue && newParameters.FvModel != _parameters.Value.FvModel)
            {
                _ = _marketMaker.CancelAllQuotesAsync(); // Fire and forget cancel

            }

            SetFairValueProvider(newParameters.FvModel);
            _logger.LogInformationWithCaller($"ASIS params => {_parameters}.");
            _parameters = newParameters;
            _logger.LogInformationWithCaller($"Updating parameters for Instrument {QuotingInstrument.Symbol}.");
            _logger.LogInformationWithCaller($"TOBE params => {newParameters}");
            return;
        }
    }

    public void SetFairValueProvider(FairValueModel model)
    {
        if (_fairValueProvider != null)
        {
            if (_fairValueProvider.Model == model)
            {
                return;
            }
            _fairValueProvider.FairValueChanged -= OnFairValueChanged;
        }

        var fairValueProvider = _fairValueProviderFactory.CreateProvider(model, QuotingInstrument);
        _logger.LogInformationWithCaller($"Swapping FairValueProvider for Instrument {QuotingInstrument.Symbol}. New FairValueMOdel: {model}");
        fairValueProvider.FairValueChanged += OnFairValueChanged;
        _fairValueProvider = fairValueProvider;
    }

    private void OnOrderBookUpdate(object? sender, OrderBook ob)
    {
        if (_fairValueProvider != null)
        {
            _fairValueProvider.Update(ob);
        }
    }

    private void OnFairValueChanged(object? sender, FairValueUpdate update)
    {
        if (update.InstrumentId == QuotingInstrument.InstrumentId)
        {
            Requote(update.FairValue);
        }
    }

    private void Requote(Price fairValue)
    {
        if (!_parameters.HasValue)
        {
            return;
        }

        QuotingParameters currentParams;
        lock (_lock)
        {
            currentParams = _parameters.Value;
        }

        if (fairValue.ToTicks() == 0) return;

        // 1. Calculate raw bid/ask prices based on spread.
        // Use Price arithmetic to avoid precision issues with decimal.
        var spreadAmountInDecimal = fairValue.ToDecimal() * currentParams.SpreadBp * 0.0001m * 0.5m;
        var spreadAmount = Price.FromDecimal(spreadAmountInDecimal);
        var rawBidPrice = fairValue - spreadAmount;
        var rawAskPrice = fairValue + spreadAmount;

        // 2. Round prices to the instrument's tick size.
        var tickSizeInTicks = QuotingInstrument.TickSize.ToTicks();
        if (tickSizeInTicks <= 0) return; // Avoid division by zero

        var roundedBidTicks = rawBidPrice.ToTicks() / tickSizeInTicks * tickSizeInTicks; // Floor
        var roundedAskTicks = (rawAskPrice.ToTicks() + tickSizeInTicks - 1) / tickSizeInTicks * tickSizeInTicks; // Ceiling

        var targetQuotePair = new QuotePair(
            QuotingInstrument.InstrumentId,
            new Quote(Price.FromTicks(roundedBidTicks), currentParams.Size),
            new Quote(Price.FromTicks(roundedAskTicks), currentParams.Size),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        // Delegate execution to the MarketMaker
        // This is a fire-and-forget call to avoid blocking the fair value update thread.
        QuotePairCalculated?.Invoke(this, targetQuotePair);
        _ = _marketMaker.UpdateQuoteTargetAsync(targetQuotePair);
    }
}