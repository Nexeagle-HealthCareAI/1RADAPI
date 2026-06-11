using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// IMemoryCache-backed module resolution. Reads the hospital's latest
/// subscription row (same "newest by CreatedAt" rule as
/// GetSubscriptionStatusQuery) and parses its Modules column.
///
/// Deliberately orthogonal to subscription lock/expiry enforcement: a locked
/// subscription still RESOLVES its modules — blocking locked tenants is the
/// existing lock flow's job. This keeps [RequiresModule] answering exactly one
/// question ("did this center buy PACS?") instead of two.
///
/// No subscription row at all → full product, matching the pre-module
/// behaviour for fresh/trial hospitals.
/// </summary>
public class ModuleEntitlementService : IModuleEntitlementService
{
    // Short TTL: an admin flipping a center's SKU takes effect within a
    // minute on every instance, with at most one DB read per hospital per TTL.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IApplicationDbContext _db;
    private readonly IMemoryCache _cache;

    public ModuleEntitlementService(IApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private static string CacheKey(Guid hospitalId) => $"modules:{hospitalId:N}";

    public async Task<IReadOnlySet<string>> GetEnabledModulesAsync(Guid hospitalId, CancellationToken cancellationToken = default)
    {
        if (hospitalId == Guid.Empty)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_cache.TryGetValue(CacheKey(hospitalId), out HashSet<string>? cached) && cached != null)
            return cached;

        var modulesRaw = await _db.HospitalSubscriptions
            .AsNoTracking()
            .Where(s => s.HospitalId == hospitalId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.Modules)
            .FirstOrDefaultAsync(cancellationToken);

        // No subscription row → full product (pre-module behaviour).
        var set = ModuleConstants.Parse(
            string.IsNullOrWhiteSpace(modulesRaw) ? ModuleConstants.DefaultModules : modulesRaw);

        _cache.Set(CacheKey(hospitalId), set, CacheTtl);
        return set;
    }

    public async Task<bool> HasModuleAsync(Guid hospitalId, string module, CancellationToken cancellationToken = default)
    {
        var set = await GetEnabledModulesAsync(hospitalId, cancellationToken);
        return set.Contains(module);
    }

    public void Invalidate(Guid hospitalId) => _cache.Remove(CacheKey(hospitalId));
}
