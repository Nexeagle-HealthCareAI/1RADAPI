namespace _1Rad.Application.Interfaces;

/// <summary>
/// Low-latency WAKE SIGNAL for the extraction worker. The durable queue is the
/// StudyAssets table itself (a row in 'Queued' is a job, claimed with a lease —
/// see <c>IApplicationDbContext.ClaimNextExtractionJobAsync</c>), which is what
/// makes extraction safe across many API instances. This signal only nudges the
/// LOCAL worker to claim immediately instead of waiting for its poll tick, so a
/// fresh upload starts processing right away. Missing a signal is harmless — the
/// worker's poll timer is the backstop, and other instances poll independently.
///
/// Singleton lifetime.
/// </summary>
public interface IDicomExtractionQueue
{
    /// <summary>Nudge the local worker to poll for work now (the row is already 'Queued' in the DB).</summary>
    void Enqueue(Guid assetId);

    /// <summary>Wait until a signal arrives or the timeout elapses (the worker's poll cadence).</summary>
    Task WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
