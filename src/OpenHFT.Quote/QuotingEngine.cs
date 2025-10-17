using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Processing;
using OpenHFT.Quoting.FairValue;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

public class QuotingEngine : IQuotingEngine
{
    private readonly ILogger _logger;
    private readonly MarketDataManager _marketDataManager;
    private readonly MarketMaker _marketMaker;
    private readonly object _lock = new();

    private IFairValueProvider _fairValueProvider;
    private QuotingParameters _parameters;

    public Instrument QuotingInstrument { get; }

    public QuotingEngine(
        ILogger logger,
        Instrument instrument,
        MarketDataManager marketDataManager,
        MarketMaker marketMaker,
        IFairValueProvider initialFairValueProvider,
        QuotingParameters initialParameters)
    {
        _logger = logger;
        QuotingInstrument = instrument;
        _marketDataManager = marketDataManager;
        _marketMaker = marketMaker;
        _fairValueProvider = initialFairValueProvider;
        _parameters = initialParameters;
    }

    public void Start()
    {
        _logger.LogInformationWithCaller($"Starting QuotingEngine for {QuotingInstrument.Symbol}.");
        _fairValueProvider.FairValueChanged += OnFairValueChanged;
        _marketDataManager.SubscribeOrderBook(QuotingInstrument.InstrumentId, $"QuotingEngine_{QuotingInstrument.Symbol}_{_fairValueProvider.Model}", OnOrderBookUpdate);
    }

    public void Stop()
    {
        _logger.LogInformationWithCaller($"Stopping QuotingEngine for {QuotingInstrument.Symbol}.");
        _marketDataManager.UnsubscribeOrderBook(QuotingInstrument.InstrumentId, $"QuotingEngine_{QuotingInstrument.Symbol}_{_fairValueProvider.Model}");
        _fairValueProvider.FairValueChanged -= OnFairValueChanged;
        _ = _marketMaker.CancelAllQuotesAsync(); // Fire and forget cancel
    }

    public void UpdateParameters(QuotingParameters newParameters)
    {
        lock (_lock)
        {
            _logger.LogInformationWithCaller($"Updating parameters for Instrument {QuotingInstrument.Symbol}.");
            _parameters = newParameters;
        }
    }

    public void SetFairValueProvider(IFairValueProvider newProvider)
    {
        lock (_lock)
        {
            _logger.LogInformationWithCaller($"Swapping FairValueProvider for Instrument {QuotingInstrument.Symbol}.");
            // Unsubscribe from the old provider
            _fairValueProvider.FairValueChanged -= OnFairValueChanged;
            // Subscribe to the new one
            _fairValueProvider = newProvider;
            _fairValueProvider.FairValueChanged += OnFairValueChanged;
        }
    }

    private void OnOrderBookUpdate(object? sender, OrderBook ob)
    {
        _fairValueProvider.Update(ob);
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
        QuotingParameters currentParams;
        lock (_lock)
        {
            currentParams = _parameters;
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
        _ = _marketMaker.UpdateQuoteTargetAsync(targetQuotePair);
    }
}