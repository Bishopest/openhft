using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Book.Core;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Processing;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting.Validators;

/// <summary>
/// A default implementation of IQuoteValidator that prevents quotes from crossing the spread (taking liquidity).
/// It subscribes to TopOfBook updates to maintain the latest market state for validation.
/// </summary>
public class DefaultQuoteValidator : IQuoteValidator
{
    private readonly ILogger<DefaultQuoteValidator> _logger;

    public DefaultQuoteValidator(
        ILogger<DefaultQuoteValidator> logger)
    {
        _logger = logger;
    }

    public TwoSidedQuoteStatus ShouldQuoteBeLive(QuotePair pair)
    {
        var bidPrice = pair.Bid.Price;
        var askPrice = pair.Ask.Price;

        return bidPrice >= askPrice
            ? new TwoSidedQuoteStatus(pair.InstrumentId, QuoteStatus.Held, QuoteStatus.Held)
            : new TwoSidedQuoteStatus(pair.InstrumentId, QuoteStatus.Live, QuoteStatus.Live);
    }
}