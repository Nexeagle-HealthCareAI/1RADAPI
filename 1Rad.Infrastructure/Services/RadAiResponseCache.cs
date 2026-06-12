using System;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// IMemoryCache-backed RadAI response cache. Process-local (fine while the API
/// runs single-instance); when it scales out, replace this registration with a
/// Redis-backed implementation and nothing else changes.
/// </summary>
public class RadAiResponseCache : IRadAiResponseCache
{
    private readonly IMemoryCache _cache;

    public RadAiResponseCache(IMemoryCache cache) => _cache = cache;

    public bool TryGet(string key, out RadAiCachedAnswer? answer)
        => _cache.TryGetValue(key, out answer);

    public void Set(string key, RadAiCachedAnswer answer, TimeSpan ttl)
        => _cache.Set(key, answer, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });
}
