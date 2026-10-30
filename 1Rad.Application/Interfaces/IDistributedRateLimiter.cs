namespace _1Rad.Application.Interfaces;

/// <summary>
/// Fixed-window request counter shared by every API instance (gap analysis P3-9).
/// </summary>
public interface IDistributedRateLimiter
{
    /// <summary>
    /// Counts one hit for <paramref name="key"/> in the current window. Returns false when
    /// this hit is over <paramref name="limit"/>; RetryAfter is the time left in the window.
    /// </summary>
    Task<(bool Allowed, TimeSpan RetryAfter)> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken ct);
}
