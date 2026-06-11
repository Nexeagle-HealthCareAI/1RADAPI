using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// One row per extracted DICOM slice. Lets the viewer load slices individually
/// (Option C in the DICOM-viewer-performance work) instead of downloading the
/// whole ZIP archive and unzipping in the browser.
///
/// Populated by <c>IDicomExtractionService</c> after a ZIP <see cref="StudyAsset"/>
/// is uploaded. A study with rows here is "extracted"; the manifest endpoint
/// uses these rows. A study with no rows falls back to the legacy ZIP path.
/// </summary>
public class StudySliceIndex : BaseEntity, IHospitalContext
{
    public Guid SliceId { get; set; } = Guid.NewGuid();

    /// <summary>The original ZIP asset this slice was extracted from.</summary>
    public Guid AssetId { get; set; }
    // Nullable since the RIS/PACS SKU split: PACS-only studies have no
    // appointment. Denormalised from the asset at extraction time.
    public Guid? AppointmentId { get; set; }
    public Guid HospitalId { get; set; }

    /// <summary>DICOM tag (0020,000E) — groups slices into a series.</summary>
    public string SeriesUID { get; set; } = string.Empty;

    /// <summary>DICOM tag (0008,0018) — unique identifier of this slice.</summary>
    public string SopInstanceUID { get; set; } = string.Empty;

    /// <summary>DICOM tag (0020,0013) — ordering within the series. Null if absent.</summary>
    public int? InstanceNumber { get; set; }

    /// <summary>DICOM tag (0008,103E) — series description.</summary>
    public string? SeriesDescription { get; set; }

    /// <summary>DICOM tag (0008,0060) — CR / CT / MR / US / etc.</summary>
    public string? Modality { get; set; }

    /// <summary>Public HTTPS URL of the extracted .dcm blob.</summary>
    public string BlobUrl { get; set; } = string.Empty;

    /// <summary>Path inside the blob container — kept for cleanup.</summary>
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>
    /// Public HTTPS URL of the per-slice JPEG thumbnail. Optional — populated
    /// only for the first slice of each series to keep storage bounded.
    /// </summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>
    /// Compact JSON of viewer-relevant DICOM tags (window center/width, pixel
    /// spacing, slice location, etc.). Cherry-picked to keep manifest small.
    /// </summary>
    public string? MetadataJson { get; set; }

    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public StudyAsset Asset { get; set; } = null!;
}
