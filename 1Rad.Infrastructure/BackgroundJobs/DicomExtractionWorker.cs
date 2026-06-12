using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Drains the DURABLE, DB-backed extraction queue (the StudyAssets table). Each
/// consumer atomically CLAIMS the next ready job with a lease
/// (<see cref="IApplicationDbContext.ClaimNextExtractionJobAsync"/>), so many API
/// instances pull DISTINCT work with zero contention (READPAST) and a crashed
/// instance's job is reclaimed once its lease expires — no in-memory queue, no
/// lost work on restart, no double-processing. While a job runs, a heartbeat
/// renews the lease; on failure the job is retried with backoff up to a cap, all
/// recorded in the row so retry survives restarts and is visible to every
/// instance.
///
/// Runs N parallel consumers (Dicom:ExtractionConcurrency, default 3). Latency:
/// a fresh upload signals the local worker to claim immediately; the poll timer
/// (Dicom:ExtractionPollSeconds) is the backstop that also picks up other
/// instances' overflow and retries whose backoff has elapsed.
/// </summary>
public class DicomExtractionWorker : BackgroundService
{
    private readonly IDicomExtractionQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DicomExtractionWorker> _logger;

    // Unique per process; identifies which instance holds a job's lease. NVARCHAR(64).
    private readonly string _owner;

    public DicomExtractionWorker(
        IDicomExtractionQueue queue,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DicomExtractionWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
        var host = Environment.MachineName;
        if (host.Length > 28) host = host.Substring(0, 28);
        _owner = $"{host}/{Guid.NewGuid():N}"; // <= 61 chars
    }

    private int LeaseSeconds => Math.Clamp(_configuration.GetValue("Dicom:ExtractionLeaseSeconds", 90), 30, 600);
    private int MaxAttempts  => Math.Clamp(_configuration.GetValue("Dicom:ExtractionMaxAttempts", 3), 1, 10);
    private TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Clamp(_configuration.GetValue("Dicom:ExtractionPollSeconds", 5), 1, 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = Math.Clamp(_configuration.GetValue("Dicom:ExtractionConcurrency", 3), 1, 16);
        _logger.LogInformation(
            "[DICOM_EXTRACT_WORKER] started owner={Owner} consumers={N} lease={Lease}s poll={Poll}s",
            _owner, concurrency, LeaseSeconds, PollInterval.TotalSeconds);

        // Let the app (and any pending migration) settle before the first claim.
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var consumers = Enumerable.Range(0, concurrency)
            .Select(i => ConsumeAsync(i, stoppingToken))
            .ToArray();
        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(int workerIndex, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? assetId;
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                assetId = await db.ClaimNextExtractionJobAsync(_owner, LeaseSeconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DICOM_EXTRACT_WORKER] consumer {Worker} claim query failed — backing off", workerIndex);
                await Sleep(PollInterval, stoppingToken);
                continue;
            }

            if (assetId == null)
            {
                // Nothing ready — wait for a wake signal or the poll tick (which
                // also surfaces other instances' overflow + due retries).
                await _queue.WaitForWorkAsync(PollInterval, stoppingToken);
                continue;
            }

            try
            {
                await ProcessClaimedAsync(assetId.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            // Loop straight back to claim the next job (no wait while work remains).
        }
    }

    private async Task ProcessClaimedAsync(Guid assetId, CancellationToken stoppingToken)
    {
        // Keep the lease alive for the duration of this (potentially long) extraction.
        using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatLoopAsync(assetId, hbCts.Token);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<IDicomExtractionService>();
            await svc.ExtractAsync(assetId, stoppingToken);
            // Success: ExtractAsync committed Extracted + cleared the lease/progress.
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-extraction — leave it Running; the lease lapses and
            // another instance (or this one on restart) reclaims it. No data lost.
            throw;
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(assetId, ex, stoppingToken);
        }
        finally
        {
            hbCts.Cancel();
            try { await heartbeat; } catch { /* ignore */ }
        }
    }

    private async Task HeartbeatLoopAsync(Guid assetId, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, LeaseSeconds / 3));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                await db.RenewExtractionLeaseAsync(assetId, _owner, LeaseSeconds, ct);
            }
        }
        catch (OperationCanceledException) { /* extraction finished or shutting down */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DICOM_EXTRACT_WORKER] heartbeat for {AssetId} failed (non-fatal)", assetId);
        }
    }

    /// <summary>
    /// ExtractAsync already marked the asset Failed. Decide retry-vs-final: bump
    /// the DURABLE attempt counter, clear the lease, and while under the cap flip
    /// it back to Queued with a backoff gate (the poll/claim picks it up once the
    /// gate elapses). All in the row, so it survives restarts and any instance
    /// can carry the retry.
    /// </summary>
    private async Task HandleFailureAsync(Guid assetId, Exception ex, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var asset = await db.StudyAssets.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == assetId, ct);
            if (asset == null) return;

            asset.ExtractionAttempts  += 1;
            asset.ExtractionLeaseOwner = null;
            asset.ExtractionLeaseUntil = null;

            var max = MaxAttempts;
            if (asset.ExtractionAttempts < max)
            {
                var backoff = TimeSpan.FromSeconds(5 * asset.ExtractionAttempts); // 5s, 10s, …
                asset.ExtractionStatus          = "Queued";
                asset.ExtractionError           = null;
                asset.ExtractionNextAttemptAt   = DateTime.UtcNow.Add(backoff);
                asset.ExtractionPhase           = null;
                asset.ExtractionProcessedSlices = 0;
                if (asset.ImagingStudyId is Guid sid)
                {
                    var study = await db.ImagingStudies.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == sid, ct);
                    if (study != null) study.Status = ImagingStudyStatus.Processing;
                }
                await db.SaveChangesAsync(ct);
                _logger.LogWarning(ex,
                    "[DICOM_EXTRACT_WORKER] asset {AssetId} failed (attempt {Attempt}/{Max}) — retrying in {Backoff}s",
                    assetId, asset.ExtractionAttempts, max, backoff.TotalSeconds);
            }
            else
            {
                asset.ExtractionStatus = "Failed"; // ExtractAsync already set this; persist cleared lease + attempts
                await db.SaveChangesAsync(ct);
                _logger.LogError(ex,
                    "[DICOM_EXTRACT_WORKER] asset {AssetId} gave up after {Max} attempts — left Failed.",
                    assetId, max);
            }
        }
        catch (Exception handleEx)
        {
            _logger.LogWarning(handleEx, "[DICOM_EXTRACT_WORKER] failure-handling for {AssetId} errored", assetId);
        }
    }

    private static async Task Sleep(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { /* shutting down */ }
    }
}
