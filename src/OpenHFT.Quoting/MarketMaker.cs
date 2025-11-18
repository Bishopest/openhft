using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

/// <summary>
/// Manages two-sided quoting for a single instrument by coordinating a bid and an ask quoter.
/// It decides whether to update or cancel quotes based on strategy targets and validation rules.
/// </summary>
public sealed class MarketMaker
{
    private readonly ILogger _logger;
    private readonly Instrument _instrument;
    private readonly IQuoter _bidQuoter;
    private readonly IQuoter _askQuoter;
    private readonly IQuoteValidator _quoteValidator;
    private readonly object _statusLock = new();


    private IQuotingStateProvider? _quotingStateProvider;
    private TwoSidedQuoteStatus _quoteStatus;
    private readonly object _targetLock = new();
    private QuotePair? _nextTargetQuotePair;
    // 0 = Idle, 1 = In Progress
    private int _isActionInProgress = 0;

    // Event to notify the engine
    public event Action? OrderFullyFilled;
    public event Action<Fill>? OrderFilled;
    /// <summary>
    /// Fired whenever the status of the bid or ask quote changes (e.g., from Held to Live).
    /// </summary>
    public event EventHandler<TwoSidedQuoteStatus>? StatusChanged;

    public TwoSidedQuoteStatus LatestStatus
    {
        get
        {
            lock (_statusLock) { return _quoteStatus; }
        }
    }

    public MarketMaker(
        ILogger logger,
        Instrument instrument,
        IQuoter bidQuoter,
        IQuoter askQuoter,
        IQuoteValidator quoteValidator)
    {
        _logger = logger;
        _instrument = instrument;
        _bidQuoter = bidQuoter;
        _askQuoter = askQuoter;
        _bidQuoter.OrderFullyFilled += OnQuoterFullyFilled;
        _bidQuoter.OrderFilled += OnQuoterFilled;
        _askQuoter.OrderFullyFilled += OnQuoterFullyFilled;
        _askQuoter.OrderFilled += OnQuoterFilled;
        _quoteValidator = quoteValidator;
        _quoteStatus = new TwoSidedQuoteStatus(instrument.InstrumentId, QuoteStatus.Held, QuoteStatus.Held);
    }

    public void SetQuotingStateProvider(IQuotingStateProvider provider)
    {
        _quotingStateProvider = provider;
    }

    /// <summary>
    /// This is the primary entry point. The quoting engine calls this method
    /// when a new target quote pair has been calculated.
    /// </summary>
    /// <param name="target">The new target quote pair from the strategy logic.</param>
    public async Task UpdateQuoteTargetAsync(QuotePair target)
    {
        lock (_targetLock)
        {
            // 새로운 목표가 도착하면, 항상 _nextTargetQuote를 최신 값으로 덮어씁니다.
            _nextTargetQuotePair = target;
        }

        // 현재 진행 중인 작업이 있는지 확인하고, 없다면 즉시 실행을 시도합니다.
        await TryProcessNextQuoteAsync();
    }

    private void OnQuoterFilled(Fill fill)
    {
        OrderFilled?.Invoke(fill);
    }

    private void OnQuoterFullyFilled()
    {
        OrderFullyFilled?.Invoke();
    }

    private async Task TryProcessNextQuoteAsync()
    {
        if (Interlocked.CompareExchange(ref _isActionInProgress, 1, 0) == 0)
        {
            QuotePair? targetToProcess = null;

            try
            {
                while (true)
                {
                    lock (_targetLock)
                    {
                        // 처리할 다음 목표를 가져오고, 큐를 비웁니다.
                        targetToProcess = _nextTargetQuotePair;
                        _nextTargetQuotePair = null;
                    }

                    if (targetToProcess == null)
                    {
                        // 처리할 목표가 더 이상 없으면 루프를 종료합니다.
                        break;
                    }

                    _logger.LogDebug("Processing new quote target: {Target}", targetToProcess);
                    await ExecuteQuoteUpdateAsync(targetToProcess.Value);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isActionInProgress, 0);
            }
        }
    }

    public async Task ExecuteQuoteUpdateAsync(QuotePair target)
    {
        if (target.InstrumentId != _instrument.InstrumentId)
        {
            _logger.LogWarningWithCaller($"Received a quote target for the wrong instrument. Expected {_instrument.InstrumentId}, got {target.InstrumentId}.");
            return;
        }

        if (_quotingStateProvider != null && !_quotingStateProvider.IsQuotingActive)
        {
            _logger.LogInformationWithCaller($"Execution skipped: Quoting on {_instrument.Symbol} is currently paused or inactive.");
            await CancelAllQuotesAsync();
            return;
        }

        // 1. Determine the desired status for each side based on validation rules.
        var pairedStatus = _quoteValidator.ShouldQuoteBeLive(target);

        // 2. Update and publish the new status.
        UpdateStatus(pairedStatus);

        // 3. Create and execute the necessary actions for each side concurrently.
        var bidActionTask = (pairedStatus.BidStatus == QuoteStatus.Live && target.Bid is not null)
            ? _bidQuoter.UpdateQuoteAsync(target.Bid.Value, target.IsPostOnly)
            : _bidQuoter.CancelQuoteAsync();

        var askActionTask = (pairedStatus.AskStatus == QuoteStatus.Live && target.Ask is not null)
            ? _askQuoter.UpdateQuoteAsync(target.Ask.Value, target.IsPostOnly)
            : _askQuoter.CancelQuoteAsync();

        try
        {
            await Task.WhenAll(bidActionTask, askActionTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"An error occurred while executing bid/ask actions for instrument {_instrument.InstrumentId}.");
        }
    }

    /// <summary>
    /// Pulls all quotes for this instrument from the market.
    /// </summary>
    public async Task CancelAllQuotesAsync()
    {
        _logger.LogInformationWithCaller($"Cancelling all quotes for symbol {_instrument.Symbol}.");
        UpdateStatus(new TwoSidedQuoteStatus(_instrument.InstrumentId, QuoteStatus.Held, QuoteStatus.Held));

        var bidCancelTask = _bidQuoter.CancelQuoteAsync();
        var askCancelTask = _askQuoter.CancelQuoteAsync();

        await Task.WhenAll(bidCancelTask, askCancelTask).ConfigureAwait(false);
    }


    private void UpdateStatus(TwoSidedQuoteStatus newStatus)
    {
        lock (_statusLock)
        {
            if (_quoteStatus.Equals(newStatus))
                return;

            _quoteStatus = newStatus;
        }

        _logger.LogDebug("Quote status changed for instrument {InstrumentId}: Bid={BidStatus}, Ask={AskStatus}",
            _instrument.InstrumentId, newStatus.BidStatus, newStatus.AskStatus);

        // Fire the event to notify any listeners (e.g., UI, risk management).
        StatusChanged?.Invoke(this, newStatus);
    }
}
