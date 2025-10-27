using System;
using OpenHFT.Gateway.Interfaces;

namespace OpenHFT.Gateway;

public class NullTimeSyncManager : ITimeSyncManager
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public void Stop()
    {
    }
}
