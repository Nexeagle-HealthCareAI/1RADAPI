using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// An image the doctor marked as key for a report (★ in the viewer). Points at
/// the exact DICOM instance, so the report, the printed key-image page, AI
/// drafting and training data all refer to the same pictures. Owned like a
/// report: by a visit study (AppointmentId + AppointmentServiceId) or by a
/// PACS study (ImagingStudyId).
/// </summary>
public class ReportKeyImage : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }

    public Guid? AppointmentId { get; set; }
    public Guid? AppointmentServiceId { get; set; }
    public Guid? ImagingStudyId { get; set; }

    /// <summary>The uploaded asset (ZIP / DICOM) the image came from.</summary>
    public Guid? StudyAssetId { get; set; }
    public string? SeriesInstanceUid { get; set; }
    public string SopInstanceUid { get; set; } = string.Empty;
    public int? FrameIndex { get; set; }
    /// <summary>Position in the series as shown in the viewer (0-based).</summary>
    public int? SliceIndex { get; set; }
    public string? Note { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A draft the AI produced for a report (from key images or dictation), kept
/// so it can be compared with what the doctor finally signed. Draft only —
/// nothing here is ever signed or shown to a patient.
/// </summary>
public class ReportAiDraft : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }

    public Guid? AppointmentId { get; set; }
    public Guid? AppointmentServiceId { get; set; }
    public Guid? ImagingStudyId { get; set; }

    /// <summary>vision | voice</summary>
    public string Source { get; set; } = "vision";
    public string? Model { get; set; }
    public string Findings { get; set; } = string.Empty;
    public string? Impression { get; set; }
    /// <summary>The key images the draft was made from (comma-separated SOP Instance UIDs).</summary>
    public string? KeyImageSopUids { get; set; }
    /// <summary>Did the doctor apply it in the editor?</summary>
    public bool Applied { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A signed (Final) report kept, de-identified, for improving the centre's AI:
/// the text with patient identifiers replaced, which study and images it
/// describes, and — when an AI draft was used — the draft and how much the
/// doctor changed it. Captured only when the centre has opted in.
/// </summary>
public class ApprovedReportCase : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public Guid DiagnosticReportId { get; set; }

    public string? Modality { get; set; }
    public string? StudyName { get; set; }
    public string? StudyInstanceUid { get; set; }
    /// <summary>JSON array of { series, sop, frame } for the key images.</summary>
    public string? KeyImagesJson { get; set; }

    public string FindingsDeidentified { get; set; } = string.Empty;
    public string? ImpressionDeidentified { get; set; }

    public Guid? AiDraftId { get; set; }
    public string? AiDraftSource { get; set; }
    /// <summary>0..1 — how close the signed findings are to the AI draft (1 = unchanged).</summary>
    public double? DraftSimilarity { get; set; }
    /// <summary>Words inserted, deleted or changed to turn the draft into the signed findings.</summary>
    public int? DraftEditDistance { get; set; }

    public string SignedContentHash { get; set; } = string.Empty;
    public DateTime SignedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
