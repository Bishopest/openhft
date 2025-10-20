using System;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

/// <summary>
/// A mock implementation of IQuoter that does not send real orders.
/// Instead, it logs the requested actions to the console and stores the latest quote.
/// </summary>
public class LogQuoter : IQuoter
{
    /// <summary>
    /// The most recent quote that was requested to be updated.
    /// Null if the last action was a cancellation.
    /// </summary>
    public Quote? LatestQuote { get; private set; }

    public Task UpdateQuoteAsync(Quote newQuote, CancellationToken cancellationToken = default)
    {
        LatestQuote = newQuote;
        return Task.CompletedTask;
    }

    public Task CancelQuoteAsync(CancellationToken cancellationToken = default)
    {
        LatestQuote = null;
        return Task.CompletedTask;
    }
}