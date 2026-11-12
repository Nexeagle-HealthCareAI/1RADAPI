using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Shared helpers for the daily jobs (gap analysis P3-9).
///
/// * Times are India time. The jobs used DateTime.Now, but the API container has no
///   TZ set, so "21:00" was 21:00 UTC = 02:30 IST and "today" was the UTC date.
/// * TryClaimAsync makes a run happen once per slot across all API instances.
/// </summary>
public static class JobSchedule
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    /// <summary>The current IST calendar date (Kind Unspecified).</summary>
    public static DateTime IstToday(DateTime utcNow) => (utcNow + Ist).Date;

    /// <summary>How long until the next hh:mm IST.</summary>
    public static TimeSpan DelayUntilIst(int hour, int minute, DateTime utcNow)
    {
        var istNow = utcNow + Ist;
        var next = istNow.Date.AddHours(hour).AddMinutes(minute);
        if (istNow >= next) next = next.AddDays(1);
        return next - istNow;
    }

    /// <summary>Run key for an interval job: the UTC block of <paramref name="hours"/> hours, e.g. "2026-10-30T12/6h".</summary>
    public static string IntervalKey(DateTime utcNow, int hours)
    {
        hours = Math.Max(1, hours);
        var blockStart = utcNow.Date.AddHours(utcNow.Hour / hours * hours);
        if (hours >= 24)
        {
            var days = hours / 24;
            var dayIndex = (long)(utcNow.Date - DateTime.UnixEpoch.Date).TotalDays / days * days;
            blockStart = DateTime.UnixEpoch.Date.AddDays(dayIndex);
        }
        return $"{blockStart:yyyy-MM-ddTHH}/{hours}h";
    }

    /// <summary>
    /// Claims (jobName, runKey). True for exactly one caller across all instances; false
    /// when another instance already ran this slot. If the JobRuns table is missing (DB
    /// script 99 not applied yet) the job runs, as before, with a warning.
    /// </summary>
    public static async Task<bool> TryClaimAsync(IApplicationDbContext db, string jobName, string runKey,
        ILogger logger, CancellationToken ct)
    {
        var claim = new JobRun { JobName = jobName, RunKey = runKey, ClaimedBy = Environment.MachineName };
        db.JobRuns.Add(claim);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(claim).State = EntityState.Detached; // don't retry it on the next save
            logger.LogInformation("{Job} {RunKey} already ran on another instance; skipping.", jobName, runKey);
            return false;
        }
        catch (DbUpdateException ex)
        {
            db.Entry(claim).State = EntityState.Detached;
            logger.LogWarning(ex, "{Job}: could not record the run claim (is DB script 99 applied?); running anyway.", jobName);
            return true;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("UX_JobRuns_Job_RunKey", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
    }
}
