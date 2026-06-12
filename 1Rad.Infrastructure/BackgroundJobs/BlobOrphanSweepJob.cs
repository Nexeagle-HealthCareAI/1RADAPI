using _1Rad.Application.Interfaces;
using _1Rad.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Reclaims orphaned blobs in the `dicom-files` container — the cleanup the
/// extraction code's comments long referenced but which never existed.
///
/// Two phases, deliberately different in risk:
///   1. STAGING SWEEP (on by default): bridge per-instance staging blobs
///      (`…/staging/…`) are write-once, consumed by extraction, then dead.
///      Deletes any older than the retention window — no DB reference check is
///      needed because nothing reads them after extraction. Low risk, pure win.
///   2. FULL ORPHAN RECONCILE (opt-in, dry-run by default): lists every blob and
///      compares against the set of URLs referenced by live StudyAsset /
///      StudySliceIndex rows (incl. derived `.jhc` frames + thumbnails). Blobs
///      with no live reference, older than a safety age, are orphans. Because a
///      reference-set miss would delete LIVE data, this phase only LOGS
///      candidates unless explicitly told to delete.
///
/// Config (all under "Dicom:OrphanSweep"):
///   Enabled (true)             — run the job at all.
///   IntervalHours (24)         — cadence.
///   StagingRetentionHours (24) — delete staging blobs older than this.
///   ReconcileEnabled (false)   — run the full reconcile phase.
///   DeleteOrphans (false)      — reconcile DELETES (vs. log-only) when true.
///   OrphanMinAgeHours (48)     — only treat blobs older than this as orphans.
/// </summary>
public class BlobOrphanSweepJob : BackgroundService
{
    private const string Container = "dicom-files";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<BlobOrphanSweepJob> _logger;

    public BlobOrphanSweepJob(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<BlobOrphanSweepJob> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Dicom:OrphanSweep:Enabled", true))
        {
            _logger.LogInformation("[BLOB_SWEEP] disabled by config — not running.");
            return;
        }

        var intervalHours = Math.Clamp(_config.GetValue("Dicom:OrphanSweep:IntervalHours", 24), 1, 168);
        // Stagger the first run a few minutes after boot so it doesn't pile onto
        // startup (extraction backfill, etc.).
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        await RunCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCycleAsync(stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var blob = scope.ServiceProvider.GetRequiredService<IBlobService>();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await SweepStagingAsync(blob, ct);

            if (_config.GetValue("Dicom:OrphanSweep:ReconcileEnabled", false))
                await ReconcileOrphansAsync(blob, db, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BLOB_SWEEP] cycle failed (will retry next interval).");
        }
    }

    // ── Phase 1: staging blobs (transient by design) ────────────────────────
    private async Task SweepStagingAsync(IBlobService blob, CancellationToken ct)
    {
        var retentionHours = Math.Clamp(_config.GetValue("Dicom:OrphanSweep:StagingRetentionHours", 24), 1, 720);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-retentionHours);
        int deleted = 0; long bytes = 0;

        await foreach (var b in blob.ListBlobsAsync(Container, prefix: null, ct))
        {
            ct.ThrowIfCancellationRequested();
            // Staging blobs carry a "/staging/" path segment (see instance-upload).
            if (!b.Name.Contains("/staging/", StringComparison.OrdinalIgnoreCase)) continue;
            if (b.LastModified.HasValue && b.LastModified.Value > cutoff) continue; // too recent
            try
            {
                await blob.DeleteBlobByNameAsync(b.Name, Container, ct);
                deleted++; bytes += b.Length;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[BLOB_SWEEP] staging delete failed for {Name}", b.Name);
            }
        }

        if (deleted > 0)
            _logger.LogInformation("[BLOB_SWEEP] staging: deleted {Count} blobs (~{MB:N1} MB) older than {Hrs}h.",
                deleted, bytes / 1_048_576.0, retentionHours);
    }

    // ── Phase 2: full orphan reconcile (opt-in; dry-run unless DeleteOrphans) ─
    private async Task ReconcileOrphansAsync(IBlobService blob, ApplicationDbContext db, CancellationToken ct)
    {
        var doDelete = _config.GetValue("Dicom:OrphanSweep:DeleteOrphans", false);
        var minAgeHours = Math.Clamp(_config.GetValue("Dicom:OrphanSweep:OrphanMinAgeHours", 48), 1, 8760);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-minAgeHours);

        // Build the referenced-blob-name set from live rows. Names are
        // container-relative paths (what ListBlobs returns), so URLs are
        // normalised the same way for an apples-to-apples compare.
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var url in db.StudyAssets.IgnoreQueryFilters()
                           .Select(a => a.BlobUrl).AsAsyncEnumerable().WithCancellation(ct))
            AddName(referenced, url);

        // Only treat the derived `.jhc` frame as live when progressive frames
        // are ENABLED. With them off (the default), existing frames are dead
        // weight → leaving them out of the referenced set lets this sweep
        // reclaim them.
        var framesEnabled = _config.GetValue("Dicom:WriteProgressiveFrames", false);
        await foreach (var s in db.StudySliceIndexes.IgnoreQueryFilters()
                           .Select(x => new { x.BlobUrl, x.ThumbnailUrl }).AsAsyncEnumerable().WithCancellation(ct))
        {
            AddName(referenced, s.BlobUrl);
            AddName(referenced, s.ThumbnailUrl);
            // Per-slice progressive preview lives beside the slice (_prev.jpg) and
            // is referenced via the slice — keep it out of the orphan set.
            AddName(referenced, Services.DicomExtractionService.PreviewUrlFromSlice(s.BlobUrl));
            if (framesEnabled)
                AddName(referenced, Services.DicomExtractionService.FrameUrlFromSlice(s.BlobUrl));
        }

        int orphans = 0; long bytes = 0;
        await foreach (var b in blob.ListBlobsAsync(Container, prefix: null, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (b.Name.Contains("/staging/", StringComparison.OrdinalIgnoreCase)) continue; // phase 1 owns these
            if (b.LastModified.HasValue && b.LastModified.Value > cutoff) continue;          // in-flight safety
            if (referenced.Contains(b.Name)) continue;                                       // live reference

            orphans++; bytes += b.Length;
            if (doDelete)
            {
                try { await blob.DeleteBlobByNameAsync(b.Name, Container, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "[BLOB_SWEEP] orphan delete failed for {Name}", b.Name); }
            }
            else if (orphans <= 20)
            {
                _logger.LogInformation("[BLOB_SWEEP] orphan candidate (dry-run): {Name}", b.Name);
            }
        }

        _logger.LogInformation(
            "[BLOB_SWEEP] reconcile: {Count} orphan blobs (~{MB:N1} MB), olderThan={Hrs}h, mode={Mode}.",
            orphans, bytes / 1_048_576.0, minAgeHours, doDelete ? "DELETED" : "DRY-RUN (set Dicom:OrphanSweep:DeleteOrphans=true to delete)");
    }

    private static void AddName(HashSet<string> set, string? url)
    {
        var name = BlobNameFromUrl(url);
        if (name != null) set.Add(name);
    }

    /// <summary>Container-relative blob name from a full blob/CDN URL.</summary>
    private static string? BlobNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var path = new Uri(url).AbsolutePath;       // /dicom-files/a/b/c.dcm
            var prefix = "/" + Container + "/";
            var idx = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            return Uri.UnescapeDataString(path[(idx + prefix.Length)..]);
        }
        catch { return null; }
    }
}
