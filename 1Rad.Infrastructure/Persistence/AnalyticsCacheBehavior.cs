using System.Data;
using System.Data.Common;
using System.Text.Json;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Persistence;

/// <summary>
/// Caches ICachedAnalyticsQuery results (gap analysis P3-9).
///
/// The finance matrix, strategic outlook and referral intelligence load a centre's
/// whole history (the Admin overview asks for all-time figures) and every open
/// dashboard re-requests them. The result is now reused while the centre's data is
/// unchanged: the cache key includes a cheap "data version" — row counts and latest
/// UpdatedAt of the tables these reports read — so any new bill, payment, expense,
/// commission, visit, service line, referrer or patient change produces a fresh result.
/// Entries also expire after 2 minutes so time-of-day figures stay current.
/// Per process, which is fine: each instance computes at most once per change.
/// </summary>
public sealed class AnalyticsCacheBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly IMemoryCache _cache;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AnalyticsCacheBehavior<TRequest, TResponse>> _logger;

    public AnalyticsCacheBehavior(IMemoryCache cache, ApplicationDbContext db, ILogger<AnalyticsCacheBehavior<TRequest, TResponse>> logger)
    {
        _cache = cache;
        _db = db;
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is not ICachedAnalyticsQuery) return await next();

        var hospitalId = _db.UserContext.HospitalId;
        if (hospitalId == Guid.Empty || !_db.Database.IsRelational()) return await next();

        string version;
        try
        {
            version = await DataVersionAsync(hospitalId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Analytics cache: could not read the data version; computing {Query} uncached.", typeof(TRequest).Name);
            return await next();
        }

        var key = $"analytics:{typeof(TRequest).FullName}:{hospitalId}:{JsonSerializer.Serialize(request, request.GetType())}:{version}";
        if (_cache.TryGetValue(key, out TResponse? cached) && cached is not null)
            return cached;

        var result = await next();
        if (result is not null)
            _cache.Set(key, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = null });
        return result;
    }

    private async Task<string> DataVersionAsync(Guid hospitalId, CancellationToken ct)
    {
        // One round trip; each part is an index seek on (HospitalId[, UpdatedAt]).
        const string sql = @"
SET NOCOUNT ON;
SELECT CONCAT_WS('|',
  (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.Invoices            WHERE HospitalId = @h),
  (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(CreatedAt), 126)) FROM dbo.Payments            WHERE HospitalId = @h),
  (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.Expenses            WHERE HospitalId = @h),
  (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.ReferralCommissions WHERE HospitalId = @h),
  (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.Appointments        WHERE HospitalId = @h),
  (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.AppointmentServices WHERE HospitalId = @h),
  (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.Referrers           WHERE HospitalId = @h),
  (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.Patients            WHERE HospitalId = @h),
  (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.CreditTransactions  WHERE HospitalId = @h)
);";
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var ambient = _db.Database.CurrentTransaction?.GetDbTransaction();
        if (ambient != null) cmd.Transaction = ambient;
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = "@h";
        p.Value = hospitalId;
        cmd.Parameters.Add(p);
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? "";
    }
}
