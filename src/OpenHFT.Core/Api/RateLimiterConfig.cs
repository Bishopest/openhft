namespace OpenHFT.Core.Api;

/// <summary>
/// Holds the configuration for API rate limiting.
/// </summary>
/// <param name="Limit">The maximum number of requests allowed in the time window.</param>
/// <param name="Window">The duration of the time window.</param>
public record RateLimiterConfig(int Limit, TimeSpan Window);