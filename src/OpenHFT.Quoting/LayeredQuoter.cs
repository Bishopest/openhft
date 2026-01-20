using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

public class LayeredQuoter : IQuoter, IDisposable
{
    private readonly ILogger _logger;
    private readonly Side _side;
    private readonly Instrument _instrument;
    private readonly LayeredQuoteManager _orderManager;

    private HittingLogic _hittingLogic = HittingLogic.AllowAll;
    private OrderBook? _cachedOrderBook;
    private Quote? _latestTargetQuote;
    private readonly IMarketDataManager _marketDataManager;

    public event Action<Fill>? OrderFilled;
    // unused
    public event Action OrderFullyFilled;

    public Quote? LatestQuote => _latestTargetQuote;

    public LayeredQuoter(
        ILogger<LayeredQuoter> logger,
        Side side,
        Instrument instrument,
        IOrderFactory orderFactory,
        IOrderGateway orderGateway,
        string bookName,
        IMarketDataManager marketDataManager,
        QuotingParameters initialParameters)
    {
        _logger = logger;
        _side = side;
        _orderManager = new LayeredQuoteManager(
            logger, side, instrument, orderFactory, orderGateway,
            bookName, initialParameters.Depth, initialParameters.GroupingBp
        );

        _instrument = instrument;
        _marketDataManager = marketDataManager;
        _orderManager.OrderFilled += OnManagerOrderFilled;
        UpdateParameters(initialParameters);
    }

    private OrderBook? GetOrderBookFast()
    {
        if (_cachedOrderBook != null)
        {
            return _cachedOrderBook;
        }

        var book = _marketDataManager.GetOrderBook(_instrument.InstrumentId);
        if (book != null)
        {
            _cachedOrderBook = book; // 찾았으면 캐싱
        }

        return _cachedOrderBook;
    }

    public async Task UpdateQuoteAsync(Quote newQuote, bool isPostOnly, Quantity? availablePosition, CancellationToken cancellationToken = default)
    {

        try
        {
            var effectivePrice = ApplyHittingLogic(newQuote.Price);
            var effectiveQuote = effectivePrice == newQuote.Price
                ? newQuote
                : new Quote(effectivePrice, newQuote.Size);

            _latestTargetQuote = effectiveQuote;
            await _orderManager.UpdateAsync(effectiveQuote, isPostOnly, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"{_side} An unexpected error occurred in LayeredQuoter during UpdateQuoteAsync");
        }
    }

    public async Task CancelQuoteAsync(CancellationToken cancellationToken = default)
    {
        _latestTargetQuote = null;
        await _orderManager.CancelAllAsync(cancellationToken);
    }

    /// <summary>
    /// adjust quote price by HittingLogic.
    /// </summary>
    private Price ApplyHittingLogic(Price targetPrice)
    {
        if (_hittingLogic == HittingLogic.AllowAll)
        {
            return targetPrice;
        }

        var book = GetOrderBookFast();
        if (book == null) return targetPrice;

        var bestBid = book.GetBestBid().price;
        var bestAsk = book.GetBestAsk().price;

        if (bestBid.ToDecimal() <= 0 || bestAsk.ToDecimal() <= 0) return targetPrice;

        var tickSize = _instrument.TickSize.ToDecimal();

        if (_side == Side.Buy)
        {
            // Buy Side Logic
            if (_hittingLogic == HittingLogic.OurBest)
            {
                if (targetPrice.ToDecimal() > bestBid.ToDecimal())
                {
                    return bestBid;
                }
            }
            else if (_hittingLogic == HittingLogic.Pennying)
            {

                if (targetPrice.ToDecimal() > bestBid.ToDecimal())
                {
                    var mostInnerOrder = _orderManager.GetMostInnerOrder();
                    if (mostInnerOrder is not null && mostInnerOrder.Price == bestBid)
                    {
                        return bestBid;
                    }

                    var pennyPrice = bestBid.ToDecimal() + tickSize;

                    if (pennyPrice >= bestAsk.ToDecimal())
                    {
                        return bestBid;
                    }

                    return Price.FromDecimal(pennyPrice);
                }
            }
        }
        else // Sell Side
        {
            // Sell Side Logic
            if (_hittingLogic == HittingLogic.OurBest)
            {
                if (targetPrice.ToDecimal() < bestAsk.ToDecimal())
                {
                    return bestAsk;
                }
            }
            else if (_hittingLogic == HittingLogic.Pennying)
            {
                if (targetPrice.ToDecimal() < bestAsk.ToDecimal())
                {
                    var mostInnerOrder = _orderManager.GetMostInnerOrder();
                    if (mostInnerOrder is not null && mostInnerOrder.Price == bestAsk)
                    {
                        return bestAsk;
                    }

                    var pennyPrice = bestAsk.ToDecimal() - tickSize;

                    if (pennyPrice <= bestBid.ToDecimal())
                    {
                        return bestAsk;
                    }

                    return Price.FromDecimal(pennyPrice);
                }
            }
        }

        return targetPrice;
    }

    public void UpdateParameters(QuotingParameters parameters)
    {
        _hittingLogic = parameters.HittingLogic;
    }

    private void OnManagerOrderFilled(Fill fill)
    {
        OrderFilled?.Invoke(fill);
    }

    public void Dispose()
    {
        _orderManager.OrderFilled -= OnManagerOrderFilled;
        _orderManager.Dispose();
    }

}
