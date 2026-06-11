using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// Lifecycle states for <see cref="ImagingStudy"/>. Deliberately minimal —
/// this is the IMAGING pipeline state (did the pixels arrive and index?),
/// not the clinical visit state, which stays on Appointment.Status.
/// </summary>
public static class ImagingStudyStatus
{
    /// <summary>Created at upload/registration; pixels staged but not yet indexed.</summary>
    public const string Received = "Received";
    /// <summary>Extraction (unzip/transcode/slice-index) is running.</summary>
    public const string Processing = "Processing";
    /// <summary>Viewable: slice index built, or a directly-viewable single file.</summary>
    public const string Ready = "Ready";
    /// <summary>Extraction failed — asset.ExtractionError has the detail.</summary>
    public const string Failed = "Failed";
}

/// <summary>
/// How an <see cref="ImagingStudy"/> came to be linked (or not) to a patient /
/// appointment. The inbox the PACS-only worklist surfaces is exactly the set of
/// <see cref="Unmatched"/> studies awaiting a human decision.
/// </summary>
public static class ImagingStudyMatchStatus
{
    /// <summary>No confident link found yet — sits in the unassigned inbox.</summary>
    public const string Unmatched = "Unmatched";
    /// <summary>Server-side matching linked it (MRN / accession / unique name).</summary>
    public const string AutoMatched = "AutoMatched";
    /// <summary>A user explicitly assigned the patient/appointment.</summary>
    public const string ManuallyAssigned = "ManuallyAssigned";
}

/// <summary>
/// First-class imaging aggregate — one row per DICOM study the cloud PACS
/// holds. This is the root the PACS side hangs off, with the Appointment as
/// an OPTIONAL link:
///
///   • RIS + PACS: the upload/bridge flow attaches the study to the matched
///     appointment (AppointmentId set) — existing behaviour, unchanged.
///   • PACS-only (no RIS module): studies exist with AppointmentId = null;
///     the study browser and reporting key off this row directly.
///
/// Identity is the DICOM StudyInstanceUID (0020,000D), unique per hospital
/// when known. It's discovered during extraction (a ZIP isn't parsed at
/// upload time), so it is null on freshly-received and legacy-backfilled
/// rows; the web Upload Center (Phase 2) will supply it at upload time.
///
/// Demographics are stored AS RECEIVED from DICOM/appointment — they are a
/// snapshot for display/matching, not a substitute for the Patient row
/// (PatientId, optional, reconciled later in PACS-only ingestion).
/// </summary>
public class ImagingStudy : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }

    /// <summary>DICOM (0020,000D). Null until extraction discovers it (or forever, for legacy rows).</summary>
    public string? StudyInstanceUID { get; set; }

    /// <summary>Optional link to the master patient record.</summary>
    public Guid? PatientId { get; set; }

    /// <summary>Patient name as received (DICOM (0010,0010) or the appointment's denormalised name).</summary>
    public string? PatientName { get; set; }

    /// <summary>DICOM (0010,0020) — the modality/RIS-assigned patient identifier, as received.</summary>
    public string? DicomPatientId { get; set; }

    public string? Modality { get; set; }
    public DateTime? StudyDate { get; set; }
    public string? StudyDescription { get; set; }

    /// <summary>DICOM (0008,0050). The bridge sets this to the 1Rad appointment id,
    /// so it's the key server-side matching uses to reconcile a study to a visit.</summary>
    public string? AccessionNumber { get; set; }

    /// <summary>See <see cref="ImagingStudyStatus"/>.</summary>
    public string Status { get; set; } = ImagingStudyStatus.Received;

    /// <summary>See <see cref="ImagingStudyMatchStatus"/>. Drives the PACS-only inbox.</summary>
    public string MatchStatus { get; set; } = ImagingStudyMatchStatus.Unmatched;

    /// <summary>Where the study came from: bridge | api-upload | sas-upload | legacy-backfill.</summary>
    public string? Source { get; set; }

    /// <summary>Optional RIS linkage — null for PACS-only studies.</summary>
    public Guid? AppointmentId { get; set; }
    public Guid? AppointmentServiceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadyAt { get; set; }

    // Navigation
    public Appointment? Appointment { get; set; }
    public Patient? Patient { get; set; }
    public ICollection<StudyAsset> Assets { get; set; } = new List<StudyAsset>();
}
