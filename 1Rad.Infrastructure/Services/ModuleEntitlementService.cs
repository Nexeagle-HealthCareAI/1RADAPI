using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// IMemoryCache-backed module resolution. Reads the hospital's latest
/// subscription row (same "newest by CreatedAt" rule as
/// GetSubscriptionStatusQuery) and parses its Modules column + PacsRemovedAt.
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
    private readonly int _graceDays;

    public ModuleEntitlementService(IApplicationDbContext db, IMemoryCache cache, IConfiguration configuration)
    {
        _db = db;
        _cache = cache;
        // Read-only grace window after PACS is removed (locked decision:
        // "studies go read-only for N days, then export-or-delete").
        _graceDays = configuration.GetValue("Pacs:GraceDays", 30);
    }

    private sealed record Entitlement(HashSet<string> Modules, DateTime? PacsRemovedAt);

    private static string CacheKey(Guid hospitalId) => $"modules:{hospitalId:N}";

    private async Task<Entitlement> GetEntitlementAsync(Guid hospitalId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey(hospitalId), out Entitlement? cached) && cached != null)
            return cached;

        var row = await _db.HospitalSubscriptions
            .AsNoTracking()
            .Where(s => s.HospitalId == hospitalId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Modules, s.PacsRemovedAt })
            .FirstOrDefaultAsync(cancellationToken);

        // No subscription row → full product (pre-module behaviour).
        var set = ModuleConstants.Parse(
            string.IsNullOrWhiteSpace(row?.Modules) ? ModuleConstants.DefaultModules : row!.Modules);

        var ent = new Entitlement(set, row?.PacsRemovedAt);
        _cache.Set(CacheKey(hospitalId), ent, CacheTtl);
        return ent;
    }

    public async Task<IReadOnlySet<string>> GetEnabledModulesAsync(Guid hospitalId, CancellationToken cancellationToken = default)
    {
        if (hospitalId == Guid.Empty)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (await GetEntitlementAsync(hospitalId, cancellationToken)).Modules;
    }

    public async Task<bool> HasModuleAsync(Guid hospitalId, string module, CancellationToken cancellationToken = default)
    {
        var set = await GetEnabledModulesAsync(hospitalId, cancellationToken);
        return set.Contains(module);
    }

    public async Task<ModuleAccess> GetModuleAccessAsync(Guid hospitalId, string module, CancellationToken cancellationToken = default)
    {
        if (hospitalId == Guid.Empty) return ModuleAccess.None;

        var ent = await GetEntitlementAsync(hospitalId, cancellationToken);
        if (ent.Modules.Contains(module)) return ModuleAccess.Full;

        // Grace applies only to PACS — the module with a downgrade lifecycle.
        if (string.Equals(module, ModuleConstants.Pacs, StringComparison.OrdinalIgnoreCase)
            && ent.PacsRemovedAt is DateTime removedAt
            && DateTime.UtcNow < removedAt.AddDays(_graceDays))
        {
            return ModuleAccess.GraceRead;
        }

        return ModuleAccess.None;
    }

    public void Invalidate(Guid hospitalId) => _cache.Remove(CacheKey(hospitalId));
}
