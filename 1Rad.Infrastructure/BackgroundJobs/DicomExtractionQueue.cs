using _1Rad.Application.Interfaces;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Wake-signal implementation of <see cref="IDicomExtractionQueue"/>. Backed by a
/// counting semaphore: <see cref="Enqueue"/> releases a permit, the worker awaits
/// one (with a timeout that doubles as its poll cadence). Durable work state lives
/// in the StudyAssets table — this only minimises pick-up latency for uploads that
/// land on THIS instance.
/// </summary>
public class DicomExtractionQueue : IDicomExtractionQueue
{
    private readonly SemaphoreSlim _signal = new(0);

    public void Enqueue(Guid assetId)
    {
        // Coalesce bursts: one pending permit is enough to wake the poller, which
        // then drains everything that's ready. Avoid unbounded permit growth.
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
        }
    }

    public async Task WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { await _signal.WaitAsync(timeout, cancellationToken); }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
