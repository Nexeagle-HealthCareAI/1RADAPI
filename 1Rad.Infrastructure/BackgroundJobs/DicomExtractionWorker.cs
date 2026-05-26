using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Drains <see cref="IDicomExtractionQueue"/> and runs extraction. Each item
/// uses a fresh DI scope so the scoped <see cref="IApplicationDbContext"/>
/// is disposed cleanly between jobs.
///
/// On startup we also requeue any assets stuck in 'Queued' or 'Running' from
/// a previous process — protects against a crash mid-extraction.
/// </summary>
public class DicomExtractionWorker : BackgroundService
{
    private readonly IDicomExtractionQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DicomExtractionWorker> _logger;

    public DicomExtractionWorker(
        IDicomExtractionQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DicomExtractionWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[DICOM_EXTRACT_WORKER] started");

        // Crash-recovery: pick up anything left in a non-terminal state.
        await RequeueStaleAsync(stoppingToken);

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
                _logger.LogInformation("[DICOM_EXTRACT_WORKER] cancellation requested, exiting");
                return;
            }
            catch (Exception ex)
            {
                // ExtractAsync already records the failure to the asset row.
                // Logging here as a safety net so the worker keeps running.
                _logger.LogError(ex, "[DICOM_EXTRACT_WORKER] extraction crashed for asset {AssetId}", assetId);
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
                .Where(a => a.FileType == "zip"
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
