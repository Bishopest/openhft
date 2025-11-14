using System;
using OpenHFT.Quoting.Models;

namespace OpenHFT.GUI.Services;

public interface IQuoteManager
{
    /// <summary>
    /// Fired when a new QuotePair update is received.
    /// </summary>
    event EventHandler<(string omsIdentifier, QuotePair pair)> OnQuoteUpdated;

    /// <summary>
    /// Gets the latest QuotePair for a given instrument.
    /// </summary>
    QuotePair? GetQuote(string omsIdentifier, int instrumentId);

}
