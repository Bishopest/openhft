using System;

namespace OpenHFT.Core.Api;

/// <summary>
/// A high-performance rate limiter based on the Sliding Window Log algorithm.
/// This class is thread-safe.
/// </summary>
public class RateLimiter
{
    private readonly int _limit;
    private readonly long _windowTicks;
    private readonly Queue<long> _timestamps;
    private readonly object _lock = new();

    public RateLimiter(RateLimiterConfig config)
    {
        _limit = config.Limit;
        _windowTicks = (long)config.Window.TotalMilliseconds;
        _timestamps = new Queue<long>(_limit);
    }

    /// <summary>
    /// Attempts to acquire a token for a request. If successful, the request is logged.
    /// </summary>
    /// <returns>True if the request is allowed, otherwise false.</returns>
    public bool TryAcquireToken()
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;

            // Remove timestamps that are outside the current window
            while (_timestamps.Count > 0 && (now - _timestamps.Peek()) > _windowTicks)
            {
                _timestamps.Dequeue();
            }

            // Check if we are under the limit
            if (_timestamps.Count < _limit)
            {
                _timestamps.Enqueue(now);
                return true;
            }

            return false;
        }
    }
}
