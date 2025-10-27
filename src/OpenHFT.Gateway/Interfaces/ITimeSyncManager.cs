using System;

namespace OpenHFT.Gateway.Interfaces;

/// <summary>
/// Defines the contract for a service that synchronizes application time with external sources.
/// </summary>
public interface ITimeSyncManager
{
    /// <summary>
    /// Starts the time synchronization process.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the time synchronization process.
    /// </summary>
    void Stop();
}