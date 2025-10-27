using Disruptor.Dsl;
using OpenHFT.Core.Collections;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Feed.Adapters;

namespace OpenHFT.Feed.Interfaces;

/// <summary>
/// Market data feed handler that normalizes and publishes events
/// </summary>
public interface IFeedHandler : IDisposable
{
    /// <summary>
    /// Get feed handler statistics
    /// </summary>
    FeedHandlerStatistics Statistics { get; }
    event EventHandler<ConnectionStateChangedEventArgs> AdapterConnectionStateChanged;
    event EventHandler<FeedErrorEventArgs> FeedError;
    event EventHandler<AuthenticationEventArgs>? AdapterAuthenticationStateChanged;

}
