using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Runs daily to manage subscription lifecycle:
/// - Marks expiring subscriptions (3 days before end)
/// - Applies 2-day grace period after expiry
/// - Locks hospitals that have exceeded the grace period with no payment
/// </summary>
public class SubscriptionLifecycleJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubscriptionLifecycleJob> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6); // Run every 6 hours

    public SubscriptionLifecycleJob(
        IServiceScopeFactory scopeFactory,
        ILogger<SubscriptionLifecycleJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[SubscriptionLifecycle] Service started.");

        // Run immediately on startup, then on interval
        await RunCycleAsync(stoppingToken);

        using PeriodicTimer timer = new(_interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCycleAsync(stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        _logger.LogInformation("[SubscriptionLifecycle] Running lifecycle check at {Time}", DateTime.UtcNow);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            // Bypass multi-tenant query filter — this job operates across ALL hospitals
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;

            // Load all subscriptions (ignore global HospitalId filter for this admin job)
            var subscriptions = await db.HospitalSubscriptions
                .IgnoreQueryFilters()
                .Where(s => s.Status != "Locked")
                .ToListAsync(ct);

            int notified = 0, expired = 0, locked = 0;

            foreach (var sub in subscriptions)
            {
                var daysRemaining = (sub.EndDate - now).TotalDays;
                var daysPastExpiry = (now - sub.EndDate).TotalDays;

                // 1. Mark as Expiring (3+ days notice before end)
                if (sub.Status == "Active" && daysRemaining <= 3 && daysRemaining > 0 && sub.NotificationSentAt == null)
                {
                    sub.Status = "Expiring";
                    sub.NotificationSentAt = now;
                    notified++;
                    _logger.LogInformation("[SubscriptionLifecycle] HospitalId={HId} marked Expiring ({Days:F1} days left)", sub.HospitalId, daysRemaining);
                }

                // 2. Trial/subscription has passed EndDate — enter grace period (still unlocked)
                if ((sub.Status == "Active" || sub.Status == "Expiring") && daysRemaining <= 0)
                {
                    sub.Status = "Expired";
                    expired++;
                    _logger.LogInformation("[SubscriptionLifecycle] HospitalId={HId} marked Expired (grace period begins)", sub.HospitalId);
                }

                // 3. Lock after 2-day grace period — check no approved payment exists
                if (sub.Status == "Expired" && !sub.IsLocked && daysPastExpiry >= 2)
                {
                    // Check if hospital has an approved payment request
                    var hasApprovedPayment = await db.SubscriptionPaymentRequests
                        .IgnoreQueryFilters()
                        .AnyAsync(r => r.HospitalId == sub.HospitalId && r.Status == "Approved" && r.CreatedAt > sub.EndDate, ct);

                    if (!hasApprovedPayment)
                    {
                        sub.IsLocked = true;
                        sub.LockedAt = now;
                        sub.LockReason = sub.IsTrial ? "TrialGracePeriodExpired" : "PaymentGracePeriodExpired";
                        sub.Status = "Locked";
                        locked++;
                        _logger.LogWarning("[SubscriptionLifecycle] HospitalId={HId} LOCKED — {Reason}", sub.HospitalId, sub.LockReason);
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[SubscriptionLifecycle] Cycle complete. Notified={N} Expired={E} Locked={L}", notified, expired, locked);

            // PACS downgrade lifecycle: studies whose read-only grace has fully
            // expired are exported-or-deleted — here, auto-deleted.
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            if (config.GetValue("Pacs:AutoDeleteEnabled", true))
            {
                var blob = scope.ServiceProvider.GetRequiredService<IBlobService>();
                var graceDays = config.GetValue("Pacs:GraceDays", 30);
                await ProcessPacsGraceExpiryAsync(db, blob, graceDays, now, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubscriptionLifecycle] Error during lifecycle check");
        }
    }

    // Permanently removes the imaging (blobs + studies + assets + slices) for
    // hospitals whose PACS read-only grace window has elapsed and that still
    // don't have PACS. Report TEXT is preserved (the report's ImagingStudyId is
    // nulled to satisfy the NoAction FK). Bounded per cycle so a large tenant
    // drains over several runs instead of blocking one cycle.
    private async Task ProcessPacsGraceExpiryAsync(
        ApplicationDbContext db, IBlobService blob, int graceDays, DateTime now, CancellationToken ct)
    {
        const int MaxStudiesPerHospitalPerCycle = 100;
        var cutoff = now.AddDays(-graceDays);

        // Newest subscription per hospital decides current modules (mirrors the
        // entitlement service's "latest by CreatedAt" rule).
        var candidates = (await db.HospitalSubscriptions
                .IgnoreQueryFilters()
                .Where(s => s.PacsRemovedAt != null && s.PacsRemovedAt < cutoff)
                .Select(s => new { s.HospitalId, s.Modules, s.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(x => x.HospitalId)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
            .Where(x => !ModuleConstants.Parse(x.Modules).Contains(ModuleConstants.Pacs))
            .ToList();

        foreach (var h in candidates)
        {
            var studies = await db.ImagingStudies
                .IgnoreQueryFilters()
                .Where(s => s.HospitalId == h.HospitalId)
                .OrderBy(s => s.CreatedAt)
                .Take(MaxStudiesPerHospitalPerCycle)
                .ToListAsync(ct);
            if (studies.Count == 0) continue;

            var studyIds = studies.Select(s => s.Id).ToList();

            // Keep report text — just detach it from the study being deleted.
            var reports = await db.DiagnosticReports
                .IgnoreQueryFilters()
                .Where(r => r.ImagingStudyId != null && studyIds.Contains(r.ImagingStudyId.Value))
                .ToListAsync(ct);
            foreach (var r in reports) r.ImagingStudyId = null;

            var assets = await db.StudyAssets
                .IgnoreQueryFilters()
                .Where(a => a.ImagingStudyId != null && studyIds.Contains(a.ImagingStudyId.Value))
                .Include(a => a.Slices)
                .ToListAsync(ct);

            var urls = new List<string>();
            foreach (var a in assets)
            {
                if (!string.IsNullOrWhiteSpace(a.BlobUrl)) urls.Add(a.BlobUrl);
                foreach (var s in a.Slices)
                {
                    if (!string.IsNullOrWhiteSpace(s.BlobUrl)) urls.Add(s.BlobUrl);
                    if (!string.IsNullOrWhiteSpace(s.ThumbnailUrl)) urls.Add(s.ThumbnailUrl);
                }
            }
            for (int i = 0; i < urls.Count; i += 16)
            {
                var batch = urls.Skip(i).Take(16)
                    .Select(async u => { try { await blob.DeleteFileAsync(u, "dicom-files"); } catch { /* best-effort */ } });
                await Task.WhenAll(batch);
            }

            db.StudyAssets.RemoveRange(assets); // cascade-deletes slices
            db.ImagingStudies.RemoveRange(studies);
            await db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "[SubscriptionLifecycle] PACS grace expired for HospitalId={HId} — deleted {S} studies, {A} assets, ~{B} blobs (report text retained).",
                h.HospitalId, studies.Count, assets.Count, urls.Count);
        }
    }
}
