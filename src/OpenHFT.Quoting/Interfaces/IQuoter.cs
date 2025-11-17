using System;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.Models;
namespace OpenHFT.Quoting.Interfaces;

/// <summary>
/// Defines the contract for an engine that manages a single-sided quote (either bid or ask) on an exchange.
/// It is responsible for submitting, updating, and cancelling the quote to maintain a presence on one side of the order book.
/// </summary>
public interface IQuoter
{
    /// <summary>
    /// Fired when the quoter's active order is fully filled.
    /// </summary>
    event Action OrderFullyFilled;

    /// <summary>
    /// Fired when the quoter's active order is filled anyway.
    /// </summary>
    event Action<Fill> OrderFilled;

    /// <summary>
    /// Submits a new quote or modifies an existing one to the specified price and size.
    /// A typical implementation would handle the logic of cancelling the previous quote 
    /// and placing a new one (a cancel/replace operation).
    /// </summary>
    /// <param name="newQuote">The target quote containing the new price and size.</param>
    /// <param name="isPostOnly">if true, order should be made with true postonly flag</param>
    /// <param name="cancellationToken">A token to signal the cancellation of the operation.</param>
    /// <returns>A task that represents the asynchronous update operation.</returns>
    Task UpdateQuoteAsync(Quote newQuote, bool isPostOnly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels any active quote currently managed by this quoter.
    /// This is used to pull the quote from the market, for example, during high volatility or shutdown.
    /// </summary>
    /// <param name="cancellationToken">A token to signal the cancellation of the operation.</param>
    /// <returns>A task that represents the asynchronous cancellation operation.</returns>
    Task CancelQuoteAsync(CancellationToken cancellationToken = default);

}
