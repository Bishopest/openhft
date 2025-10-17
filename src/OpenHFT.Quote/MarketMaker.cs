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

    private TwoSidedQuoteStatus _quoteStatus;

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
        _quoteValidator = quoteValidator;
        _quoteStatus = new TwoSidedQuoteStatus(instrument.InstrumentId, QuoteStatus.Held, QuoteStatus.Held);
    }

    /// <summary>
    /// This is the primary entry point. The quoting engine calls this method
    /// when a new target quote pair has been calculated.
    /// </summary>
    /// <param name="target">The new target quote pair from the strategy logic.</param>
    public async Task UpdateQuoteTargetAsync(QuotePair target)
    {
        if (target.InstrumentId != _instrument.InstrumentId)
        {
            _logger.LogWarningWithCaller($"Received a quote target for the wrong instrument. Expected {_instrument.InstrumentId}, got {target.InstrumentId}.");
            return;
        }

        // 1. Determine the desired status for each side based on validation rules.
        var bidStatus = _quoteValidator.ShouldQuoteBeLive(target.Bid, Side.Buy)
            ? QuoteStatus.Live
            : QuoteStatus.Held;

        var askStatus = _quoteValidator.ShouldQuoteBeLive(target.Ask, Side.Sell)
            ? QuoteStatus.Live
            : QuoteStatus.Held;

        // 2. Update and publish the new status.
        UpdateStatus(new TwoSidedQuoteStatus(_instrument.InstrumentId, bidStatus, askStatus));

        // 3. Create and execute the necessary actions for each side concurrently.
        var bidActionTask = (bidStatus == QuoteStatus.Live)
            ? _bidQuoter.UpdateQuoteAsync(target.Bid)
            : _bidQuoter.CancelQuoteAsync();

        var askActionTask = (askStatus == QuoteStatus.Live)
            ? _askQuoter.UpdateQuoteAsync(target.Ask)
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
