using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Drains <see cref="IDicomExtractionQueue"/> and runs extraction. Each item
/// uses a fresh DI scope so the scoped <see cref="IApplicationDbContext"/>
/// is disposed cleanly between jobs.
///
/// Runs N PARALLEL consumers (configurable: Dicom:ExtractionConcurrency,
/// default 3) so two studies uploaded together don't extract one-after-another
/// — the channel hands each asset to exactly one consumer, so no item is
/// processed twice. On startup we also requeue any extractable asset stuck in
/// 'Queued'/'Running' from a previous process (crash-recovery).
/// </summary>
public class DicomExtractionWorker : BackgroundService
{
    private readonly IDicomExtractionQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DicomExtractionWorker> _logger;

    // File types the extraction pipeline normalises (must match
    // DicomExtractionService / StudyController.NeedsExtraction). Crash-recovery
    // requeues stuck assets of ALL these types — not just ZIP — so a bridge
    // per-instance or single-DCM upload can't get stranded in 'Queued'/'Running'
    // after an API restart.
    private static readonly string[] ExtractableTypes = { "zip", "instances", "dcm", "dicom" };

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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Crash-recovery: pick up anything left in a non-terminal state.
        await RequeueStaleAsync(stoppingToken);

        var concurrency = Math.Clamp(_configuration.GetValue("Dicom:ExtractionConcurrency", 3), 1, 16);
        _logger.LogInformation("[DICOM_EXTRACT_WORKER] started with {N} parallel consumers", concurrency);

        // Spawn N consumers competing on the same channel. Each item is
        // delivered to exactly one of them (System.Threading.Channels), and each
        // uses its own DI scope per asset, so DbContext is never shared across
        // threads.
        var consumers = Enumerable.Range(0, concurrency)
            .Select(i => ConsumeAsync(i, stoppingToken))
            .ToArray();
        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(int workerIndex, CancellationToken stoppingToken)
    {
        await foreach (var assetId in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<IDicomExtractionService>();
                await svc.ExtractAsync(assetId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("[DICOM_EXTRACT_WORKER] consumer {Worker} cancelled, exiting", workerIndex);
                return;
            }
            catch (Exception ex)
            {
                // ExtractAsync already records the failure to the asset row.
                // Logging here as a safety net so the consumer keeps running.
                _logger.LogError(ex, "[DICOM_EXTRACT_WORKER] consumer {Worker} extraction crashed for asset {AssetId}", workerIndex, assetId);
            }
        }
    }

    private async Task RequeueStaleAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var stale = await db.StudyAssets
                .IgnoreQueryFilters()
                .Where(a => a.FileType != null
                         && ExtractableTypes.Contains(a.FileType.ToLower())
                         && (a.ExtractionStatus == "Queued" || a.ExtractionStatus == "Running"))
                .Select(a => a.Id)
                .ToListAsync(ct);

            foreach (var id in stale)
            {
                _logger.LogInformation("[DICOM_EXTRACT_WORKER] requeuing stale asset {AssetId}", id);
                _queue.Enqueue(id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DICOM_EXTRACT_WORKER] stale-requeue check failed (continuing)");
        }
    }
}
