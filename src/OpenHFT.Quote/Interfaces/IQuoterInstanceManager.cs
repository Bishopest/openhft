using System;

namespace OpenHFT.Quoting.Interfaces;

public interface IQuotingInstanceManager
{
    /// <summary>
    /// Deploys and starts a new strategy for a given instrument.
    /// If a strategy for the instrument already exists, it will be replaced.
    /// </summary>
    /// <param name="parameters">The quoting parameters for the new strategy.</param>
    /// <returns>True if deployment was successful, false otherwise.</returns>
    bool DeployStrategy(QuotingParameters parameters);

    /// <summary>
    /// Stops and removes the strategy for a given instrument.
    /// </summary>
    /// <param name="instrumentId">The ID of the instrument whose strategy should be retired.</param>
    /// <returns>True if a strategy was found and retired, false otherwise.</returns>
    bool RetireStrategy(int instrumentId);

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
