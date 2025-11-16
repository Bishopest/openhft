using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

/// <summary>
/// A mock implementation of IQuoter that does not send real orders.
/// Instead, it logs the requested actions to the console and stores the latest quote.
/// </summary>
public class LogQuoter : IQuoter
{
    private readonly ILogger<LogQuoter> _logger;

    public event Action OrderFullyFilled;

    public LogQuoter(ILogger<LogQuoter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The most recent quote that was requested to be updated.
    /// Null if the last action was a cancellation.
    /// </summary>
    public Quote? LatestQuote { get; private set; }

    public Task UpdateQuoteAsync(Quote newQuote, bool isPostOnly, CancellationToken cancellationToken = default)
    {
        LatestQuote = newQuote;
        _logger.LogInformationWithCaller($"Shooting for quote: {newQuote}");
        return Task.CompletedTask;
    }

    public Task CancelQuoteAsync(CancellationToken cancellationToken = default)
    {
        LatestQuote = null;
        _logger.LogInformationWithCaller($"Cancel all quotes");
        return Task.CompletedTask;
    }
}