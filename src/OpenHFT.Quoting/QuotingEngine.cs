using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Processing;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

public class QuotingEngine : IQuotingEngine, IQuotingStateProvider
{
    private readonly ILogger _logger;
    private readonly IMarketDataManager _marketDataManager;
    private readonly MarketMaker _marketMaker;
    private readonly object _lock = new();
    private IFairValueProvider? _fairValueProvider;
    private QuotingParameters _parameters;
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
    // Skew-related state fields
    private long _unappliedBuyFillsInTicks = 0;
    private long _unappliedSellFillsInTicks = 0;
    private long? _groupingTickMultiple;
    private readonly object _groupingLock = new();
    public event EventHandler<QuotePair>? QuotePairCalculated;
    public event EventHandler<QuotingParameters>? ParametersUpdated;
    public event EventHandler<Fill>? EngineOrderFilled;

    public bool IsQuotingActive => IsActive && !_isPausedByFill;

    public QuotingEngine(
        ILogger logger,
        Instrument instrument,
        MarketMaker marketMaker,
        IFairValueProvider fairValueProvider,
        QuotingParameters initialParameters,
        IMarketDataManager marketDataManager)
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
        if (_parameters.HasCoreParameterChanges(newParameters))
        {
            _logger.LogWarningWithCaller($"Attempted to update immutable parameters. A new QuotingEngine instance is required. Old: {_parameters}, New: {newParameters}");
            return;
        }

        lock (_lock)
        {
            if (_parameters.GroupingBp != newParameters.GroupingBp)
            {
                _groupingTickMultiple = null;
            }

            if (newParameters.Equals(_parameters)) return;

            _logger.LogInformation("Updating tunable parameters for {Symbol}: {NewParams}",
                QuotingInstrument.Symbol, newParameters);
            _parameters = newParameters;
            _marketMaker.UpdateParameters(newParameters);
        }

    }

    private void OnFairValueChanged(object? sender, FairValueUpdate update)
    {
        if (update.InstrumentId == _parameters.FairValueSourceInstrumentId)
        {
            Requote(update);
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

        EngineOrderFilled?.Invoke(this, fill);

        var fillQuantityInTicks = fill.Quantity.ToTicks();

        if (fill.Side == Side.Buy)
        {
            // 매수 체결이 들어오면, 먼저 _unappliedSellFillsInTicks와 상계하고,
            // 남은 양을 _unappliedBuyFillsInTicks에 더합니다.
            InterlockedNetAndAdd(
                ref _unappliedBuyFillsInTicks,
                ref _unappliedSellFillsInTicks,
                fillQuantityInTicks
            );
            // Increase the absolute buy fills.
            Interlocked.Add(ref _totalBuyFillsInTicks, fillQuantityInTicks);
            // Decrease the absolute sell fills, but not below zero.
            // InterlockedDecrementToZero(ref _totalSellFillsInTicks, fillQuantityInTicks);
            _logger.LogInformationWithCaller($"Buy fill received for {QuotingInstrument.Symbol}. Quantity: {fill.Quantity}. New total buy: {TotalBuyFills}. New Unapplied buy: {_unappliedBuyFillsInTicks}");
        }
        else // Side.Sell
        {
            // 매도 체결이 들어오면, 먼저 _unappliedBuyFillsInTicks와 상계하고,
            // 남은 양을 _unappliedSellFillsInTicks에 더합니다.
            InterlockedNetAndAdd(
                ref _unappliedSellFillsInTicks,
                ref _unappliedBuyFillsInTicks,
                fillQuantityInTicks
            );

            // Increase the absolute sell fills.
            Interlocked.Add(ref _totalSellFillsInTicks, fillQuantityInTicks);
            // Decrease the absolute buy fills, but not below zero.
            // InterlockedDecrementToZero(ref _totalBuyFillsInTicks, fillQuantityInTicks);
            _logger.LogInformationWithCaller($"Sell fill received for {QuotingInstrument.Symbol}. Quantity: {fill.Quantity}. New total sell: {TotalSellFills}. New Unapplied sell: {_unappliedSellFillsInTicks}");
        }
    }

    private void ApplySkew()
    {
        QuotingParameters currentParams;
        lock (_lock)
        {
            currentParams = _parameters;
        }

        var orderSizeInTicks = currentParams.Size.ToTicks();
        if (orderSizeInTicks <= 0) return;

        var currentUnappliedBuys = Interlocked.Read(ref _unappliedBuyFillsInTicks);
        var currentUnappliedSells = Interlocked.Read(ref _unappliedSellFillsInTicks);

        // 1. 매수 체결에 대한 Skew 계산
        long buyMultiples = currentUnappliedBuys / orderSizeInTicks;
        if (buyMultiples >= 1)
        {
            // 사용한 만큼의 체결량을 차감
            long usedAmount = buyMultiples * orderSizeInTicks;
            Interlocked.Add(ref _unappliedBuyFillsInTicks, -usedAmount);

            // SkewBp * 배수(N) 만큼 스프레드 조정
            decimal skewAdjustment = currentParams.SkewBp * buyMultiples;

            var newBidSpreadBp = currentParams.BidSpreadBp - skewAdjustment;
            var newAskSpreadBp = currentParams.AskSpreadBp - skewAdjustment;

            _logger.LogWarningWithCaller($"Buy fill threshold reached ({buyMultiples}x). Applying skew of {-skewAdjustment}bp. New BidSpread: {newBidSpreadBp}, New AskSpread: {newAskSpreadBp}");

            // 새로운 파라미터 객체 생성 및 업데이트
            UpdateSkewedParameters(newBidSpreadBp, newAskSpreadBp);
        }

        // 2. 매도 체결에 대한 Skew 계산
        long sellMultiples = currentUnappliedSells / orderSizeInTicks;
        if (sellMultiples >= 1)
        {
            long usedAmount = sellMultiples * orderSizeInTicks;
            Interlocked.Add(ref _unappliedSellFillsInTicks, -usedAmount);

            decimal skewAdjustment = currentParams.SkewBp * sellMultiples;

            // 매도 체결 시에는 스프레드를 '증가'시킴
            var newBidSpreadBp = currentParams.BidSpreadBp + skewAdjustment;
            var newAskSpreadBp = currentParams.AskSpreadBp + skewAdjustment;

            _logger.LogWarningWithCaller($"Sell fill threshold reached ({sellMultiples}x). Applying skew of {skewAdjustment}bp. New BidSpread: {newBidSpreadBp}, New AskSpread: {newAskSpreadBp}");

            UpdateSkewedParameters(newBidSpreadBp, newAskSpreadBp);
        }
    }

    private QuotePair ApplyGrouping(QuotePair target)
    {
        // Grouping BP가 0이거나 설정 안됐으면 원본 반환
        if (_parameters.GroupingBp <= 0) return target;

        // 초기화 안됐으면 계산 (Bid/Ask 중 아무거나 사용, 보통 Bid 기준)
        if (_groupingTickMultiple == null)
        {
            var refPrice = target.Bid?.Price ?? target.Ask?.Price;
            if (refPrice.HasValue)
            {
                _groupingTickMultiple = CalculateGroupingMultiple(refPrice.Value);
                _logger.LogInformationWithCaller($"Calculated {QuotingInstrument.Symbol} grouping multiple {_groupingTickMultiple} ticks.");
            }
        }

        if (_groupingTickMultiple == null || _groupingTickMultiple <= 1) return target;

        var tickSize = QuotingInstrument.TickSize.ToTicks();
        var groupSizeInTicks = tickSize * _groupingTickMultiple.Value;

        // Bid Grouping (Floor)
        Quote? newBid = null;
        if (target.Bid.HasValue)
        {
            var bidTicks = target.Bid.Value.Price.ToTicks();
            var groupedBidTicks = (bidTicks / groupSizeInTicks) * groupSizeInTicks;
            newBid = new Quote(Price.FromTicks(groupedBidTicks), target.Bid.Value.Size);
        }

        // Ask Grouping (Ceiling)
        Quote? newAsk = null;
        if (target.Ask.HasValue)
        {
            var askTicks = target.Ask.Value.Price.ToTicks();
            // Ceiling: (x + n - 1) / n * n
            var groupedAskTicks = ((askTicks + groupSizeInTicks - 1) / groupSizeInTicks) * groupSizeInTicks;
            newAsk = new Quote(Price.FromTicks(groupedAskTicks), target.Ask.Value.Size);
        }

        return new QuotePair(target.InstrumentId, newBid, newAsk, target.CreationTimestamp, target.IsPostOnly);
    }

    private long? CalculateGroupingMultiple(Price refPrice)
    {
        // 1bp = 0.0001
        var targetBpValue = refPrice.ToDecimal() * (_parameters.GroupingBp * 0.0001m);
        var tickSizeDec = QuotingInstrument.TickSize.ToDecimal();

        if (targetBpValue < tickSizeDec) return 1;

        var multiple = (long)Math.Round(targetBpValue / tickSizeDec);
        return Math.Max(1, multiple);
    }

    private void UpdateSkewedParameters(decimal newBidSpreadBp, decimal newAskSpreadBp)
    {
        QuotingParameters newParams;
        lock (_lock)
        {
            // 기존 파라미터를 기반으로 새로운 파라미터 객체를 생성
            newParams = new QuotingParameters(
                _parameters.InstrumentId, _parameters.BookName, _parameters.FvModel, _parameters.FairValueSourceInstrumentId,
                newAskSpreadBp, newBidSpreadBp, // <-- 변경된 스프레드
                _parameters.SkewBp, _parameters.Size, _parameters.Depth, _parameters.Type, _parameters.PostOnly,
                _parameters.MaxCumBidFills, _parameters.MaxCumAskFills, _parameters.HittingLogic, _parameters.GroupingBp
            );
            _parameters = newParams;
        }

        ParametersUpdated?.Invoke(this, newParams);
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

    /// <summary>
    /// Atomically nets a new fill quantity against an opposing fill counter,
    /// and adds any remainder to the target fill counter.
    /// </summary>
    /// <param name="targetFills">The counter to add the remainder to (e.g., _unappliedBuyFillsInTicks).</param>
    /// <param name="oppositeFills">The counter to net against first (e.g., _unappliedSellFillsInTicks).</param>
    /// <param name="newFillAmountInTicks">The quantity of the new fill.</param>
    private void InterlockedNetAndAdd(ref long targetFills, ref long oppositeFills, long newFillAmountInTicks)
    {
        long amountToNet = newFillAmountInTicks;

        // --- 1. 반대편 잔여량과 상계 ---
        while (amountToNet > 0)
        {
            long originalOppositeValue = Interlocked.Read(ref oppositeFills);
            if (originalOppositeValue == 0)
            {
                // 상계할 반대편 잔여량이 없으면 루프 종료
                break;
            }

            // 차감할 양 계산 (반대편 잔여량을 0 이하로 내릴 수 없음)
            long reduction = Math.Min(originalOppositeValue, amountToNet);

            // Compare-and-swap (CAS) 루프: 다른 스레드가 값을 변경했으면 재시도
            if (Interlocked.CompareExchange(ref oppositeFills, originalOppositeValue - reduction, originalOppositeValue) == originalOppositeValue)
            {
                // 상계 성공. 남은 체결량 업데이트
                amountToNet -= reduction;
            }
            // CAS 실패 시, 루프는 재시도 (다음 originalOppositeValue 읽기부터)
        }

        // --- 2. 상계 후 남은 양을 자신 쪽에 누적 ---
        if (amountToNet > 0)
        {
            Interlocked.Add(ref targetFills, amountToNet);
        }
    }

    private void Requote(FairValueUpdate fairValueUpdate)
    {
        ApplySkew();

        QuotingParameters currentParams;
        lock (_lock)
        {
            currentParams = _parameters;
        }

        if (fairValueUpdate.FairAskValue.ToTicks() == 0 || fairValueUpdate.FairBidValue.ToTicks() == 0) return;

        // 1. Calculate raw bid/ask prices based on spread.
        // Use Price arithmetic to avoid precision issues with decimal.
        var askSpreadAmountInDecimal = fairValueUpdate.FairAskValue.ToDecimal() * currentParams.AskSpreadBp * 0.0001m;
        var bidSpreadAmountInDecimal = fairValueUpdate.FairBidValue.ToDecimal() * currentParams.BidSpreadBp * 0.0001m;
        var askSpreadAmount = Price.FromDecimal(askSpreadAmountInDecimal);
        var bidSpreadAmount = Price.FromDecimal(bidSpreadAmountInDecimal);
        var rawAskPrice = fairValueUpdate.FairAskValue + askSpreadAmount;
        var rawBidPrice = fairValueUpdate.FairBidValue + bidSpreadAmount;

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

        var groupedQuotePair = ApplyGrouping(targetQuotePair);

        // Delegate execution to the MarketMaker
        // This is a fire-and-forget call to avoid blocking the fair value update thread.
        QuotePairCalculated?.Invoke(this, groupedQuotePair);

        if (_isPausedByFill)
        {
            return;
        }

        if (IsActive)
        {
            _ = _marketMaker.UpdateQuoteTargetAsync(groupedQuotePair);
        }
    }
}