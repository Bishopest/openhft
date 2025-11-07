using System;

namespace OpenHFT.Quoting.Interfaces;

public interface IQuotingInstanceManager
{
    /// <summary>
    /// update quoting parameters for a given instrument.
    /// if a instance for the instrument already exists, activate(equal params) or re deploy(different params))
    /// </summary>
    /// <param name="newParameters"></param>
    /// <returns>updated instance if success, otherwise null.</returns>
    QuotingInstance? UpdateInstanceParameters(QuotingParameters newParameters);

    /// <summary>
    /// Stops and removes the strategy for a given instrument.
    /// </summary>
    /// <param name="instrumentId">The ID of the instrument whose strategy should be retired.</param>
    /// <returns>True if a strategy was found and retired, false otherwise.</returns>
    bool RetireInstance(int instrumentId);

    /// <summary>
    /// Gets the status or instance of the currently active strategy for an instrument.
    /// </summary>
    /// <param name="instrumentId">The ID of the instrument.</param>
    /// <returns>The active strategy instance, or null if not found.</returns>
    QuotingInstance? GetInstance(int instrumentId);

    /// <summary>
    /// Gets all currently active strategy instances.
    /// </summary>
    /// <returns>A collection of all active strategies.</returns>
    IReadOnlyCollection<QuotingInstance> GetAllInstances();
}
