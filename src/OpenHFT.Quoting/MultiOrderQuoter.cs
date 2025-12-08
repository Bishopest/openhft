using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;
using OpenHFT.Quoting.Models;

namespace OpenHFT.Quoting;

/// <summary>
/// An implementation of IQuoter that uses an OrdersOnGroup instance to manage
/// a cluster of layered orders for a single quote side.
/// </summary>
public sealed class MultiOrderQuoter : IQuoter, IDisposable
{
    private readonly ILogger _logger;
    private readonly Side _side;
    private readonly OrdersOnGroup _orderGroup;

    private HittingLogic _hittingLogic = HittingLogic.AllowAll;
    private Quote? _latestTargetQuote;

    public event Action? OrderFullyFilled;
    public event Action<Fill>? OrderFilled;

    public Quote? LatestQuote => _latestTargetQuote;

    public MultiOrderQuoter(
        ILogger<MultiOrderQuoter> logger,
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

        // Create and configure the underlying order group manager.
        _orderGroup = new OrdersOnGroup(
            logger,
            instrument,
            side,
            orderFactory,
            orderGateway,
            bookName,
            marketDataManager,
            initialParameters.Depth,
            initialParameters.GroupingBp
        );

        // Wire up events from the group to this quoter's public events.
        _orderGroup.OrderFilled += OnGroupOrderFilled;
        _orderGroup.OrderFullyFilled += OnGroupOrderFullyFilled;

        // Set initial parameters
        UpdateParameters(initialParameters);
    }

    /// <summary>
    /// Updates the target quote for the order group.
    /// The OrdersOnGroup instance will handle the logic of creating, replacing, or cancelling
    /// layered orders to match the new target.
    /// </summary>
    public async Task UpdateQuoteAsync(Quote newQuote, bool isPostOnly, CancellationToken cancellationToken = default)
    {
        _latestTargetQuote = newQuote;

        try
        {
            await _orderGroup.UpdateAsync(newQuote, _hittingLogic, isPostOnly, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"({_side}) An unexpected error occurred in MultiOrderQuoter during UpdateQuoteAsync.");
        }
    }

    /// <summary>
    /// Cancels all orders managed by the underlying order group.
    /// </summary>
    public async Task CancelQuoteAsync(CancellationToken cancellationToken = default)
    {
        _latestTargetQuote = null;
        await _orderGroup.CancelAllAsync(cancellationToken);
    }

    /// <summary>
    /// Updates the tunable parameters for the quoter.
    /// Note: Depth and GroupingBp are considered immutable for the lifetime of this instance.
    /// </summary>
    public void UpdateParameters(QuotingParameters parameters)
    {
        _hittingLogic = parameters.HittingLogic;
        // Other parameters like size are handled within OrdersOnGroup via the targetQuote.
    }

    private void OnGroupOrderFilled(Fill fill)
    {
        OrderFilled?.Invoke(fill);
    }

    private void OnGroupOrderFullyFilled()
    {
        // This event might need more nuanced logic in a multi-order context.
        // For now, it fires when all orders in the group are filled/gone.
        OrderFullyFilled?.Invoke();
    }

    public void Dispose()
    {
        // Ensure event handlers are unwired to prevent memory leaks
        _orderGroup.OrderFilled -= OnGroupOrderFilled;
        _orderGroup.OrderFullyFilled -= OnGroupOrderFullyFilled;

        // Dispose the underlying group, which should cancel any remaining orders.
        _orderGroup.Dispose();
    }
}