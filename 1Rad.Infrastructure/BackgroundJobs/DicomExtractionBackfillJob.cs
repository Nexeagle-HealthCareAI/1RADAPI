using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// One-shot backfill: on startup (after a small grace delay) walks every legacy
/// ZIP <see cref="_1Rad.Domain.Entities.StudyAsset"/> that has no
/// <c>ExtractionStatus</c> and enqueues it for the
/// <see cref="DicomExtractionWorker"/> to process.
///
/// Throttled — enqueues in small batches with a delay between them so the
/// extraction worker isn't slammed with thousands of jobs on startup. This
/// matters because each extraction downloads + unzips + re-uploads slices,
/// which is bandwidth-heavy.
/// </summary>
public class DicomExtractionBackfillJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDicomExtractionQueue _queue;
    private readonly ILogger<DicomExtractionBackfillJob> _logger;

    // Grace period so the API has time to come up and serve traffic before we
    // start chewing through legacy data. Tune via config if needed.
    private static readonly TimeSpan StartupDelay     = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BatchDelay       = TimeSpan.FromSeconds(5);
    private const int BatchSize = 20;

    public DicomExtractionBackfillJob(
        IServiceScopeFactory scopeFactory,
        IDicomExtractionQueue queue,
        ILogger<DicomExtractionBackfillJob> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

            var totalEnqueued = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await db.StudyAssets
                    .IgnoreQueryFilters()
                    .Where(a => a.FileType == "zip" && string.IsNullOrEmpty(a.ExtractionStatus))
                    .OrderBy(a => a.UploadedAt)
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken);

                if (batch.Count == 0)
                {
                    _logger.LogInformation("[DICOM_BACKFILL] complete — {Total} legacy assets enqueued.", totalEnqueued);
                    return;
                }

                foreach (var asset in batch)
                {
                    asset.ExtractionStatus = "Queued";
                    _queue.Enqueue(asset.Id);
                    totalEnqueued++;
                }
                await db.SaveChangesAsync(stoppingToken);

                _logger.LogInformation("[DICOM_BACKFILL] enqueued batch of {Batch}; running total {Total}.",
                    batch.Count, totalEnqueued);

                try
                {
                    await Task.Delay(BatchDelay, stoppingToken);
                }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DICOM_BACKFILL] failed");
        }
    }
}
