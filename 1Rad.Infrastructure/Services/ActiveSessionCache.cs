using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace _1Rad.Infrastructure.Services;

// IMemoryCache-backed implementation of the active-session cache.
//
// Three observations drive the simple design:
//
//  1. Lookups are vastly more frequent than mutations. Every authenticated
//     request hits IsActive(); MarkActive/Revoke fire on login/logout only.
//
//  2. Cold reads (cache miss) MUST NOT be treated as "active". The cache is
//     authoritative only for the positive direction. The middleware should
//     fall back to a DB lookup on miss and then prime the cache. We don't
//     do that fallback HERE — that's the middleware's job — but the
//     contract is "IsActive returns true only when we have a positive
//     entry that's still within its expiry window."
//
//  3. The cache survives the lifetime of the process. On startup the
//     middleware re-primes on demand. This is fine because forced-logout
//     correctness comes from the DB; the cache is an optimisation.
public class ActiveSessionCache : IActiveSessionCache
{
    private readonly IMemoryCache _cache;
    private const string Prefix = "rad:session:";
    private static readonly TimeSpan MaxPositiveAge = TimeSpan.FromMinutes(2);

    public ActiveSessionCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    public void MarkActive(Guid sessionId, DateTime expiresAt)
    {
        // Positive entries live at most MaxPositiveAge (gap analysis P3-9). The cache
        // is per process: a sign-out handled by one API instance only clears THAT
        // instance's entry, so another instance kept honouring the revoked session
        // until the refresh token expired (days). Capping the age forces a DB
        // re-check every couple of minutes on every instance.
        var cap = DateTime.UtcNow + MaxPositiveAge;
        var until = DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc);
        _cache.Set(Key(sessionId), true, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = new DateTimeOffset(until < cap ? until : cap),
        });
    }

    public void Revoke(Guid sessionId)
    {
        _cache.Remove(Key(sessionId));
    }

    public bool IsActive(Guid sessionId)
    {
        return _cache.TryGetValue<bool>(Key(sessionId), out var active) && active;
    }

    private static string Key(Guid sessionId) => Prefix + sessionId.ToString("N");
}
