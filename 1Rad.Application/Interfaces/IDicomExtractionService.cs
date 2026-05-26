namespace _1Rad.Application.Interfaces;

/// <summary>
/// Extracts a ZIP-packaged DICOM study into individual per-slice blobs and
/// populates <c>StudySliceIndexes</c>. Used by the Option C viewer pipeline
/// to eliminate the in-browser unzip step.
///
/// Idempotent: calling twice on the same asset is safe — the existing slice
/// index is wiped (along with its blobs) before re-extraction.
/// </summary>
public interface IDicomExtractionService
{
    /// <summary>
    /// Runs extraction for the given asset. Returns the number of slices
    /// indexed, or 0 if the asset was non-DICOM (e.g., JPG/PNG report
    /// attachment). Throws on hard failure; the caller should set
    /// <c>ExtractionStatus = Failed</c> and record the error.
    /// </summary>
    Task<int> ExtractAsync(Guid assetId, CancellationToken cancellationToken);
}
