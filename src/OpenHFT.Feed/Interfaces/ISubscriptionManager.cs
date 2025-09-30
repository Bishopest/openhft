using System;

namespace OpenHFT.Feed.Interfaces;

public interface ISubscriptionManager
{
    /// <summary>
    /// Initializes subscriptions based on the application's configuration.
    /// This should be called once at startup.
    /// </summary>
    Task InitializeSubscriptionsAsync(CancellationToken cancellationToken = default);
}