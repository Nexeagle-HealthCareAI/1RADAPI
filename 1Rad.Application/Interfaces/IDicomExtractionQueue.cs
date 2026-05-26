namespace _1Rad.Application.Interfaces;

/// <summary>
/// In-process queue of assets awaiting DICOM extraction. The controller's
/// <c>/upload-complete</c> handler enqueues here so it can return to the
/// uploader immediately; a hosted background service drains it.
///
/// Singleton lifetime. Unbounded — extraction throughput &gt; upload throughput
/// in practice, and bounding would risk dropping work on burst uploads.
/// </summary>
public interface IDicomExtractionQueue
{
    void Enqueue(Guid assetId);
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}
