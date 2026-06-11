using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// IMemoryCache-backed storage metering. Usage = SUM(StudyAssets.StorageBytes)
/// for the hospital (IgnoreQueryFilters so background callers work); allowance
/// comes from the latest subscription row, same "newest by CreatedAt" rule as
/// ModuleEntitlementService. 60s TTL keeps the upload hot path off the DB while
/// staying fresh enough that a center hitting its cap is blocked within a
/// minute; upload endpoints Invalidate() on success to tighten that further.
/// </summary>
public class StorageMeteringService : IStorageMeteringService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IApplicationDbContext _db;
    private readonly IMemoryCache _cache;

    public StorageMeteringService(IApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private static string CacheKey(Guid hospitalId) => $"storage:{hospitalId:N}";

    public async Task<StorageUsage> GetUsageAsync(Guid hospitalId, CancellationToken cancellationToken = default)
    {
        if (hospitalId == Guid.Empty) return new StorageUsage();

        if (_cache.TryGetValue(CacheKey(hospitalId), out StorageUsage? cached) && cached != null)
            return cached;

        var usedBytes = await _db.StudyAssets
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.HospitalId == hospitalId)
            .SumAsync(a => (long?)a.StorageBytes, cancellationToken) ?? 0L;

        var includedGb = await _db.HospitalSubscriptions
            .AsNoTracking()
            .Where(s => s.HospitalId == hospitalId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.IncludedStorageGb)
            .FirstOrDefaultAsync(cancellationToken);

        var usage = new StorageUsage { UsedBytes = usedBytes, IncludedStorageGb = includedGb };
        _cache.Set(CacheKey(hospitalId), usage, CacheTtl);
        return usage;
    }

    public async Task<bool> IsOverQuotaAsync(Guid hospitalId, CancellationToken cancellationToken = default)
    {
        var usage = await GetUsageAsync(hospitalId, cancellationToken);
        return usage.IsOverQuota;
    }

    public void Invalidate(Guid hospitalId) => _cache.Remove(CacheKey(hospitalId));
}
