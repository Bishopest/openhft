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
    private readonly MarketDataManager _marketDataManager;
    private readonly MarketMaker _marketMaker;
    private readonly object _lock = new();
    private IFairValueProvider? _fairValueProvider;
    private QuotingParameters _parameters;
    public event EventHandler<QuotePair>? QuotePairCalculated;
    public Instrument QuotingInstrument { get; }
    public QuotingParameters CurrentParameters => _parameters;
    public bool IsActive { get; private set; }
    private volatile bool _isPausedByFill = false;
    private Timer? _pauseTimer;
    private readonly object _pauseLock = new object();
    private long _totalBuyFillsInTicks;
    private long _totalSellFillsInTicks;
    // Public properties to expose the values as Quantity structs.
    public Quantity TotalBuyFills => Quantity.FromTicks(Interlocked.Read(ref _totalBuyFillsInTicks));
    public Quantity TotalSellFills => Quantity.FromTicks(Interlocked.Read(ref _totalSellFillsInTicks));


    public QuotingEngine(
        ILogger logger,
        Instrument instrument,
        MarketMaker marketMaker,
        IFairValueProvider fairValueProvider,
        QuotingParameters initialParameters,
        MarketDataManager marketDataManager)
    {
        _logger = logger;
        QuotingInstrument = instrument;
        _marketMaker = marketMaker;
        _marketMaker.OrderFilled += OnFill;
        _fairValueProvider = fairValueProvider;
        _parameters = initialParameters;
        _marketDataManager = marketDataManager;

    }

    public void Start()
    {
        if (_fairValueProvider == null)
        {
            _logger.LogWarningWithCaller($"Please set fair value provider first for {QuotingInstrument.Symbol}");
            return;
        }

        IsActive = false;

        _logger.LogInformationWithCaller($"Starting QuotingEngine for {QuotingInstrument.Symbol}.");
        _fairValueProvider.FairValueChanged += OnFairValueChanged;
        var fvInstrumentId = _parameters.FairValueSourceInstrumentId;
        var subscriptionKey = $"QuotingEngine_{QuotingInstrument.Symbol}_{_fairValueProvider.Model}";

        switch (_fairValueProvider)
        {
            case IOrderBookConsumerProvider obProvider:
                _logger.LogInformationWithCaller("Subscribing to full OrderBook for fair value calculation");
                _marketDataManager.SubscribeOrderBook(fvInstrumentId, subscriptionKey, (sender, book) => obProvider.Update(book));
                break;
            case IBestOrderBookConsumerProvider bobProvider:
                _logger.LogInformationWithCaller("Subscribing to Best OrderBook for fair value calculation");
                _marketDataManager.SubscribeBestOrderBook(fvInstrumentId, subscriptionKey, (sender, bob) => bobProvider.Update(bob));
                break;
            default:
                _logger.LogWarningWithCaller($"FairValueProvider for {_fairValueProvider.Model} does not implement a known data consumer interface.");
                break;
        }
    }

    public void Stop()
    {
        Deactivate();

        if (_fairValueProvider == null)
        {
            _logger.LogWarningWithCaller($"Can not stop fv provider because of null fair value provider {QuotingInstrument.Symbol}");
            return;
        }
        var fvInstrumentId = _parameters.FairValueSourceInstrumentId;
        var subscriptionKey = $"QuotingEngine_{QuotingInstrument.Symbol}_{_fairValueProvider.Model}";
        switch (_fairValueProvider)
        {
            case IOrderBookConsumerProvider obProvider:
                _logger.LogInformationWithCaller("Unsubscribing to full OrderBook for fair value calculation");
                _marketDataManager.UnsubscribeOrderBook(fvInstrumentId, subscriptionKey);
                break;
            case IBestOrderBookConsumerProvider bobProvider:
                _logger.LogInformationWithCaller("Unsubscribing to Best OrderBook for fair value calculation");
                _marketDataManager.UnsubscribeBestOrderBook(fvInstrumentId, subscriptionKey);
                break;
            default:
                _logger.LogWarningWithCaller($"FairValueProvider for {_fairValueProvider.Model} does not implement a known data consumer interface.");
                break;
        }

        _fairValueProvider.FairValueChanged -= OnFairValueChanged;
    }

    public void Deactivate()
    {
        _logger.LogInformationWithCaller($"Pausing QuotingEngine for {QuotingInstrument.Symbol}.");
        IsActive = false;
        _ = _marketMaker.CancelAllQuotesAsync(); // Fire and forget cancel
    }

    public void Activate()
    {
        _logger.LogInformationWithCaller($"Resume QuotingEngine for {QuotingInstrument.Symbol}.");
        IsActive = true;
    }

    public void UpdateParameters(QuotingParameters newParameters)
    {
        // Validate that the core, immutable properties match.
        if (newParameters.InstrumentId != _parameters.InstrumentId ||
            newParameters.FvModel != _parameters.FvModel ||
            newParameters.FairValueSourceInstrumentId != _parameters.FairValueSourceInstrumentId ||
            newParameters.Type != _parameters.Type)
        {
            _logger.LogWarningWithCaller($"Attempted to update immutable parameters. A new QuotingEngine instance is required. Old: {_parameters}, New: {newParameters}");
            return;
        }

        lock (_lock)
        {
            if (newParameters.Equals(_parameters)) return;

            _logger.LogInformation("Updating tunable parameters for {Symbol}: {NewParams}",
                QuotingInstrument.Symbol, newParameters);
            _parameters = newParameters;
        }

    }

    private void OnFairValueChanged(object? sender, FairValueUpdate update)
    {
        if (update.InstrumentId == _parameters.FairValueSourceInstrumentId)
        {
            Requote(update.FairValue);
        }
    }

    public void PauseQuoting(TimeSpan duration)
    {
        lock (_pauseLock)
        {
            _logger.LogWarningWithCaller($"Quoting for {QuotingInstrument.Symbol} is being paused for {duration.TotalSeconds} seconds");
            _isPausedByFill = true;
            _pauseTimer?.Dispose();
            _pauseTimer = new Timer(ResumeQuotingAfterPause, null, duration, Timeout.InfiniteTimeSpan);
        }

    }

    private void ResumeQuotingAfterPause(object? state)
    {
        _logger.LogInformationWithCaller($"Quoting pause for {QuotingInstrument.Symbol} has been resumed");
        _isPausedByFill = false;
    }

    /// <summary>
    /// Processes a fill event to update the internal absolute fill counters in a thread-safe manner.
    /// </summary>
    /// <param name="fill">The fill event from the exchange.</param>
    public void OnFill(Fill fill)
    {
        if (fill.InstrumentId != QuotingInstrument.InstrumentId)
        {
            // This fill is not for us.
            return;
        }

        var fillQuantityInTicks = fill.Quantity.ToTicks();

        if (fill.Side == Side.Buy)
        {
            // Increase the absolute buy fills.
            Interlocked.Add(ref _totalBuyFillsInTicks, fillQuantityInTicks);
            // Decrease the absolute sell fills, but not below zero.
            // InterlockedDecrementToZero(ref _totalSellFillsInTicks, fillQuantityInTicks);
            _logger.LogInformationWithCaller($"Buy fill received for {QuotingInstrument.Symbol}. Quantity: {fill.Quantity}. New total buy: {TotalBuyFills}");

        }
        else // Side.Sell
        {
            // Increase the absolute sell fills.
            Interlocked.Add(ref _totalSellFillsInTicks, fillQuantityInTicks);
            // Decrease the absolute buy fills, but not below zero.
            // InterlockedDecrementToZero(ref _totalBuyFillsInTicks, fillQuantityInTicks);
            _logger.LogInformationWithCaller($"Sell fill received for {QuotingInstrument.Symbol}. Quantity: {fill.Quantity}. New total sell: {TotalSellFills}");

        }
    }

    /// <summary>
    /// Atomically decrements the target value by the amount, ensuring it does not go below zero.
    /// </summary>
    private void InterlockedDecrementToZero(ref long target, long amount)
    {
        long originalValue;
        long newValue;
        do
        {
            originalValue = Interlocked.Read(ref target);
            newValue = Math.Max(0, originalValue - amount);

            // Compare the original value with the current value. If they are the same,
            // it means no other thread has changed it in the meantime, so we can safely update it.
            // If they are different, it means another thread interfered, so we loop and try again.
        }
        while (Interlocked.CompareExchange(ref target, newValue, originalValue) != originalValue);
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
        var askSpreadAmountInDecimal = fairValue.ToDecimal() * currentParams.AskSpreadBp * 0.0001m;
        var bidSpreadAmountInDecimal = fairValue.ToDecimal() * currentParams.BidSpreadBp * 0.0001m;
        var askSpreadAmount = Price.FromDecimal(askSpreadAmountInDecimal);
        var bidSpreadAmount = Price.FromDecimal(bidSpreadAmountInDecimal);
        var rawAskPrice = fairValue + askSpreadAmount;
        var rawBidPrice = fairValue + bidSpreadAmount;

        // 2. Round prices to the instrument's tick size.
        var tickSizeInTicks = QuotingInstrument.TickSize.ToTicks();
        if (tickSizeInTicks <= 0) return; // Avoid division by zero

        var roundedAskTicks = (rawAskPrice.ToTicks() + tickSizeInTicks - 1) / tickSizeInTicks * tickSizeInTicks; // Ceiling
        var roundedBidTicks = rawBidPrice.ToTicks() / tickSizeInTicks * tickSizeInTicks; // Floor

        var cumAskFillViolated = _parameters.MaxCumAskFills < TotalSellFills;
        var cumBidFillViolated = _parameters.MaxCumBidFills < TotalBuyFills;

        var targetQuotePair = new QuotePair(
            QuotingInstrument.InstrumentId,
            cumBidFillViolated ? null : new Quote(Price.FromTicks(roundedBidTicks), currentParams.Size),
            cumAskFillViolated ? null : new Quote(Price.FromTicks(roundedAskTicks), currentParams.Size),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            currentParams.PostOnly
        );

        // Delegate execution to the MarketMaker
        // This is a fire-and-forget call to avoid blocking the fair value update thread.
        QuotePairCalculated?.Invoke(this, targetQuotePair);

        if (_isPausedByFill)
        {
            return;
        }

        if (IsActive)
        {
            _ = _marketMaker.UpdateQuoteTargetAsync(targetQuotePair);
        }
    }
}