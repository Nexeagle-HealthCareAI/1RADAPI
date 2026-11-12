using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using _1Rad.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace _1RadAPI.Live;

/// <summary>
/// Turns "every browser polls every 12–60 s" into "each API instance checks once every
/// few seconds per centre that has someone connected, and pushes when something
/// changed". Works with any number of instances and no message bus: each instance
/// watches the database for the centres connected to IT.
///
/// Per centre, one query returns a version string per topic (row count + latest
/// UpdatedAt of the tables behind it). A topic whose version moved is pushed to the
/// centre's group as <c>changed(["appointments", …])</c>.
/// </summary>
public sealed class LiveChangeWatcher : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);

    // topic -> SQL expression producing that topic's version for @h
    private static readonly (string Topic, string Sql)[] Topics =
    {
        ("appointments", @"CONCAT(
            (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.Appointments WHERE HospitalId = @h), '|',
            (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.AppointmentServices WHERE HospitalId = @h), '|',
            (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(CreatedAt), 126)) FROM dbo.AppointmentComments WHERE HospitalId = @h))"),
        ("approvals", @"(SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(COALESCE(UpdatedAt, CreatedAt)), 126)) FROM dbo.ApprovalRequests WHERE HospitalId = @h)"),
        ("finance", @"CONCAT(
            (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.Invoices WHERE HospitalId = @h), '|',
            (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(CreatedAt), 126)) FROM dbo.Payments WHERE HospitalId = @h), '|',
            (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.Expenses WHERE HospitalId = @h), '|',
            (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.ReferralCommissions WHERE HospitalId = @h), '|',
            (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.CreditTransactions WHERE HospitalId = @h))"),
        ("reports", @"(SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.DiagnosticReports WHERE HospitalId = @h)"),
        ("patients", @"(SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(UpdatedAt), 126)) FROM dbo.Patients WHERE HospitalId = @h)"),
        ("studies", @"CONCAT(
            (SELECT CONCAT(COUNT_BIG(*), '@', CONVERT(varchar(33), MAX(COALESCE(ReadyAt, CreatedAt)), 126), '@', SUM(CASE WHEN Status = 'Ready' THEN 1 ELSE 0 END)) FROM dbo.ImagingStudies WHERE HospitalId = @h), '|',
            (SELECT CONCAT(COUNT_BIG(*), '@', SUM(CAST(ISNULL(ExtractionProcessedSlices, 0) AS bigint)), '@', SUM(CASE WHEN ExtractionStatus = 'Extracted' THEN 1 ELSE 0 END)) FROM dbo.StudyAssets WHERE HospitalId = @h))"),
    };

    private static readonly string Sql =
        "SET NOCOUNT ON; SELECT " + string.Join(", ", Topics.Select((t, i) => $"{t.Sql} AS t{i}")) + ";";

    private readonly IServiceScopeFactory _scopes;
    private readonly IHubContext<LiveHub> _hub;
    private readonly LiveConnectionRegistry _registry;
    private readonly ILogger<LiveChangeWatcher> _logger;
    private readonly ConcurrentDictionary<Guid, string[]> _last = new();

    public LiveChangeWatcher(IServiceScopeFactory scopes, IHubContext<LiveHub> hub,
        LiveConnectionRegistry registry, ILogger<LiveChangeWatcher> logger)
    {
        _scopes = scopes;
        _hub = hub;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var centres = _registry.ActiveCentres();
            // Forget centres nobody watches any more, so a reconnect starts from a fresh baseline.
            foreach (var gone in _last.Keys.Except(centres).ToList()) _last.TryRemove(gone, out _);
            if (centres.Count == 0) continue;

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                foreach (var hospitalId in centres)
                {
                    var now = await ReadVersionsAsync(db, hospitalId, stoppingToken);
                    if (_last.TryGetValue(hospitalId, out var before))
                    {
                        var changed = Topics.Where((t, i) => now[i] != before[i]).Select(t => t.Topic).ToArray();
                        if (changed.Length > 0)
                            await _hub.Clients.Group(LiveHub.GroupFor(hospitalId)).SendAsync("changed", changed, stoppingToken);
                    }
                    _last[hospitalId] = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live change watcher tick failed; browsers keep their fallback polling.");
            }
        }
    }

    private static async Task<string[]> ReadVersionsAsync(ApplicationDbContext db, Guid hospitalId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = "@h";
        p.Value = hospitalId;
        cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new string[Topics.Length];
        if (await reader.ReadAsync(ct))
            for (var i = 0; i < Topics.Length; i++)
                result[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
        return result;
    }
}
