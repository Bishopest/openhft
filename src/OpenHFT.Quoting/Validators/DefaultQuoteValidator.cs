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
        // 1. Ask와 Bid가 모두 null인 경우 (Nothing to quote)
        if (pair.Bid is null && pair.Ask is null)
        {
            return new TwoSidedQuoteStatus(pair.InstrumentId, QuoteStatus.Held, QuoteStatus.Held);
        }

        // 2. Ask와 Bid가 모두 null이 아닌 경우 (Price Check)
        if (pair.Bid is not null && pair.Ask is not null)
        {
            // BidPrice와 AskPrice 추출 (Quote 구조체 내에 Price 필드가 있다고 가정)
            var bidPrice = pair.Bid.Value.Price;
            var askPrice = pair.Ask.Value.Price;

            // 2-1. BidPrice가 AskPrice보다 크거나 같으면 (크로스 오버 또는 제로 스프레드) 둘 다 Held
            // 크로스 오버(Crossed Market)는 유효하지 않음
            if (bidPrice >= askPrice)
            {
                return new TwoSidedQuoteStatus(pair.InstrumentId, QuoteStatus.Held, QuoteStatus.Held);
            }

            // 2-2. BidPrice < AskPrice (정상 스프레드)
            return new TwoSidedQuoteStatus(pair.InstrumentId, QuoteStatus.Live, QuoteStatus.Live);
        }

        // 3. 한쪽만 null인 경우 (Partial Quote)
        // 이 지점에서는 Bid와 Ask 중 정확히 하나만 null입니다.

        // 3-1. Bid가 null이고 Ask만 있는 경우
        if (pair.Bid is null)
        {
            return new TwoSidedQuoteStatus(pair.InstrumentId, QuoteStatus.Held, QuoteStatus.Live);
        }

        // 3-2. Ask가 null이고 Bid만 있는 경우
        if (pair.Ask is null)
        {
            return new TwoSidedQuoteStatus(pair.InstrumentId, QuoteStatus.Live, QuoteStatus.Held);
        }

        // 모든 케이스를 처리했으므로 여기에 도달할 수 없지만, 컴파일러를 위해 추가
        return new TwoSidedQuoteStatus(pair.InstrumentId, QuoteStatus.Held, QuoteStatus.Held);
    }
}