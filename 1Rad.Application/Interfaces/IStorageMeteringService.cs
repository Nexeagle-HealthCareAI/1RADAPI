namespace _1Rad.Application.Interfaces;

/// <summary>
/// A hospital's PACS storage position: persisted bytes vs. the subscription's
/// allowance. <see cref="IncludedStorageGb"/> null = unmetered (legacy plans).
/// </summary>
public class StorageUsage
{
    public long UsedBytes { get; set; }
    public int? IncludedStorageGb { get; set; }
    public long? IncludedBytes => IncludedStorageGb.HasValue
        ? IncludedStorageGb.Value * 1024L * 1024L * 1024L
        : null;
    public bool IsOverQuota => IncludedBytes.HasValue && UsedBytes >= IncludedBytes.Value;
    public double? PercentUsed => IncludedBytes is > 0
        ? Math.Round(100.0 * UsedBytes / IncludedBytes.Value, 1)
        : null;
}

/// <summary>
/// PACS storage metering (Phase 3 of the RIS/PACS SKU split). Usage is the
/// SUM of StudyAssets.StorageBytes for the hospital, cached briefly — quota
/// checks sit on the upload hot path. Enforcement policy: over-quota blocks
/// NEW DICOM ingestion only; viewing existing studies is never blocked.
/// </summary>
public interface IStorageMeteringService
{
    Task<StorageUsage> GetUsageAsync(Guid hospitalId, CancellationToken cancellationToken = default);
    Task<bool> IsOverQuotaAsync(Guid hospitalId, CancellationToken cancellationToken = default);
    /// <summary>Drop the cached usage (call after an upload/extraction changes it).</summary>
    void Invalidate(Guid hospitalId);
}
