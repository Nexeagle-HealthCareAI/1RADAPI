using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// IDistributedRateLimiter over dbo.RateLimitCounters (DB script 100). One atomic
/// upsert-and-increment per hit (UPDLOCK + HOLDLOCK, like SequenceCounters), so the
/// count is exact across instances. If the table is missing (script 100 not yet
/// applied) or the database is unreachable it fails OPEN with a warning: a limiter
/// outage must not lock everyone out of signing in.
/// </summary>
public sealed class SqlRateLimiter : IDistributedRateLimiter
{
    // Process-local fallback for the EF InMemory provider (tests).
    private static readonly ConcurrentDictionary<(string, DateTime), int> InMemory = new();

    private readonly ApplicationDbContext _db;
    private readonly ILogger<SqlRateLimiter> _logger;

    public SqlRateLimiter(ApplicationDbContext db, ILogger<SqlRateLimiter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<(bool Allowed, TimeSpan RetryAfter)> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var windowStart = new DateTime(now.Ticks - now.Ticks % window.Ticks, DateTimeKind.Utc);
        var retryAfter = windowStart + window - now;
        if (key.Length > 200) key = key[..200];

        int hits;
        if (!_db.Database.IsRelational())
        {
            hits = InMemory.AddOrUpdate((key, windowStart), 1, (_, c) => c + 1);
            return (hits <= limit, retryAfter);
        }

        const string sql = @"
SET NOCOUNT ON;
DECLARE @v INT;
BEGIN TRANSACTION;
    UPDATE dbo.RateLimitCounters WITH (UPDLOCK, HOLDLOCK)
       SET @v = Hits = Hits + 1
     WHERE LimitKey = @k AND WindowStart = @w;
    IF @@ROWCOUNT = 0
    BEGIN
        SET @v = 1;
        INSERT INTO dbo.RateLimitCounters (LimitKey, WindowStart, Hits) VALUES (@k, @w, 1);
    END
COMMIT TRANSACTION;
-- Housekeeping: roughly 1 call in 200 clears windows older than a day.
IF ABS(CHECKSUM(NEWID())) % 200 = 0
    DELETE TOP (5000) FROM dbo.RateLimitCounters WHERE WindowStart < DATEADD(DAY, -1, SYSUTCDATETIME());
SELECT @v;";

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var ambient = _db.Database.CurrentTransaction?.GetDbTransaction();
            if (ambient != null) cmd.Transaction = ambient;

            DbParameter P(string n, object v) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v; return p; }
            cmd.Parameters.Add(P("@k", key));
            cmd.Parameters.Add(P("@w", windowStart));

            hits = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Rate limiter unavailable for {Key}; allowing the request (is DB script 100 applied?).", key);
            return (true, TimeSpan.Zero);
        }

        return (hits <= limit, retryAfter);
    }
}
