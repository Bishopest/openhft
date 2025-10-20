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
public class DefaultQuoteValidator : IQuoteValidator, IDisposable
{
    private readonly ILogger<DefaultQuoteValidator> _logger;
    private readonly Instrument _instrument;
    private readonly MarketDataManager _marketDataManager;
    private readonly object _lock = new();

    // Volatile to ensure the latest value is read across threads.
    private volatile BestOrderBook? _latestBBO;

    public DefaultQuoteValidator(
        ILogger<DefaultQuoteValidator> logger,
        Instrument instrument,
        MarketDataManager marketDataManager)
    {
        _logger = logger;
        _instrument = instrument;
        _marketDataManager = marketDataManager;

        // Subscribe to ToB updates to get the latest market prices.
        _marketDataManager.SubscribeBestOrderBook(
            _instrument.InstrumentId,
            $"DefaultQuoteValidator_{_instrument.Symbol}",
            OnBestOrderBookUpdate
        );
    }

    private void OnBestOrderBookUpdate(object? sender, BestOrderBook bbo)
    {
        lock (_lock)
        {
            _latestBBO = bbo;
        }
    }

    /// <summary>
    /// Determines if a given quote is valid to be placed on the market.
    /// This implementation checks if the quote would take liquidity (cross the spread).
    /// </summary>
    /// <param name="quote">The quote to validate.</param>
    /// <param name="side">The side of the quote.</param>
    /// <returns>True if the quote is passive (does not cross), false otherwise.</returns>
    public bool ShouldQuoteBeLive(Quote quote, Side side)
    {
        BestOrderBook currentMarket;

        if (_latestBBO == null)
        {
            _logger.LogWarningWithCaller($"Can not validate quote because of null best orderbook on {_instrument.Symbol}");
            return false;
        }

        lock (_lock)
        {
            currentMarket = _latestBBO;
        }

        if (side == Side.Buy)
        {
            var (bestAskPrice, bestAskQuantity) = currentMarket.GetBestAsk();
            if (bestAskPrice.ToDecimal() > 0 && quote.Price >= bestAskPrice)
            {
                _logger.LogInformationWithCaller($"Holding BID quote for {_instrument.Symbol}: Price {quote.Price} would cross market ask {bestAskPrice}");
                return false;
            }
        }
        else // side == Side.Sell
        {
            var (bestBidPrice, bestBidQuantity) = currentMarket.GetBestBid();
            if (bestBidPrice.ToDecimal() > 0 && quote.Price <= bestBidPrice)
            {
                _logger.LogInformationWithCaller($"Holding ASK quote for {_instrument.Symbol}: Price {quote.Price} would cross market bid {bestBidPrice}");
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        _marketDataManager.UnsubscribeBestOrderBook(
            _instrument.InstrumentId,
            $"DefaultQuoteValidator_{_instrument.Symbol}"
        );
    }
}