using System;

namespace OpenHFT.Quoting.Interfaces;

public interface IQuotingStateProvider
{
    /// <summary>
    /// Checks if the quoting logic is currently active and allowed to send orders.
    /// </summary>
    bool IsQuotingActive { get; }
}
