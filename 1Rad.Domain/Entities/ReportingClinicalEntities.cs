using _1Rad.Domain.Common;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace _1Rad.Domain.Entities
{
    public class ReportTemplate : BaseEntity, IHospitalContext
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string Modality { get; set; } = string.Empty; // X-RAY, MRI, etc.
        public string Content { get; set; } = string.Empty; 
        
        public Guid HospitalId { get; set; }
        
        // Navigation
        public Hospital Hospital { get; set; } = null!;
    }

    public class ReportingKeyword : BaseEntity, IHospitalContext
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Trigger { get; set; } = string.Empty; // e.g. .norm
        public string ReplacementText { get; set; } = string.Empty; // Full findings text
        public string Category { get; set; } = string.Empty; // e.g. Liver, GB, Heart, etc.
        
        public Guid DoctorId { get; set; }
        public Guid HospitalId { get; set; }
        
        // Navigation
        public User Doctor { get; set; } = null!;
        public Hospital Hospital { get; set; } = null!;
    }

    public class DiagnosticReport : BaseEntity, IHospitalContext
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        // A report belongs to exactly ONE of: an appointment (RIS / RIS+PACS)
        // XOR an imaging study (Cloud PACS-only, no visit). Both are nullable;
        // exactly one is set. AppointmentId became nullable in the PACS-only
        // split — legacy rows always have it.
        public Guid? AppointmentId { get; set; }

        /// <summary>Set instead of <see cref="AppointmentId"/> for appointment-free
        /// (PACS-only) reports written directly against an <see cref="ImagingStudy"/>.</summary>
        public Guid? ImagingStudyId { get; set; }

        // Multi-service support (migration 57). Each AppointmentService
        // (line item of work) has its own report. NULL on legacy rows
        // that pre-date the migration; backfill stamps these from the
        // 1:1 single-service-per-appointment relationship.
        public Guid? AppointmentServiceId { get; set; }

        public Guid? DoctorId { get; set; }
        public Guid? TemplateId { get; set; }
        
        public string Findings { get; set; } = string.Empty; // Can be JSON for structured
        public string Impression { get; set; } = string.Empty;
        public string Advice { get; set; } = string.Empty;
        
        public bool IsFinalized { get; set; } = false;
        public DateTime? FinalizedAt { get; set; }

        // ── Electronic sign-off state machine (21 CFR Part 11) ─────────────
        // Draft → Preliminary → Final → Addended. Stored as a string to match
        // the codebase's other status columns (Appointment.Status,
        // ApprovalRequest.Status). IsFinalized is kept as a derived back-compat
        // shim — it's true exactly when Status is Final or Addended, so the
        // worklist query and offline cache that already read IsFinalized keep
        // working without change. See ReportStatuses for the allowed values.
        public string Status { get; set; } = ReportStatuses.Draft;

        // Identity-bound signature. SignedByUserId is the AUTHENTICATED user who
        // signed (not a typed name); SignerName/SignerCredentials are snapshots
        // captured at signing so the signature block stays accurate even if the
        // user later edits their profile. SignedAt is the server clock at the
        // moment of signing — the authoritative signature timestamp.
        public Guid? SignedByUserId { get; set; }
        public string? SignerName { get; set; }
        public string? SignerCredentials { get; set; }
        public DateTime? SignedAt { get; set; }

        // SHA-256 of the report content captured at signing (canonical
        // Findings|Impression|Advice). Lets us prove after the fact that the
        // stored content still matches what was signed — tamper evidence.
        public string? SignedContentHash { get; set; }

        public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
        // Updated on every save so the client can reliably tell whether its
        // local autosaved draft is newer/older than the server copy
        // (drives the crash-recovery prompt in ReportingPage).
        public DateTime? UpdatedAt { get; set; }
        // Tombstone for the Phase B1 sync engine. Soft-deleted reports are
        // hidden from regular handlers (filter is applied in the sync delta
        // query, not here) but exposed to the client as a tombstone so the
        // local cache can purge them.
        public DateTime? DeletedAt { get; set; }
        // Optimistic-concurrency token (Phase B2 Track 3). SQL Server
        // maintains it automatically via ROWVERSION; EF treats it as a
        // concurrency check on UPDATE. The frontend reads it from the
        // DTO, stores it locally, and sends it back on save so two users
        // editing in parallel detect the conflict instead of silently
        // overwriting each other.
        public byte[]? RowVersion { get; set; }
        public string ReportPdfUrl { get; set; } = string.Empty;
        public string ReportingMode { get; set; } = "Structured"; // Structured or Narrative Editor
        public int? FieldCount { get; set; } = 0;
        
        public Guid HospitalId { get; set; }
        
        // Navigation
        [JsonIgnore]
        public Appointment? Appointment { get; set; }
        [JsonIgnore]
        public ImagingStudy? ImagingStudy { get; set; }
        [JsonIgnore]
        public User Doctor { get; set; } = null!;
        [JsonIgnore]
        public Hospital Hospital { get; set; } = null!;
        [JsonIgnore]
        public ReportTemplate? Template { get; set; }
        public ICollection<DiagnosticReportField> Fields { get; set; } = new List<DiagnosticReportField>();

        // Formal amendments appended AFTER finalisation. Each addendum is its
        // own immutable record (the signed Findings/Impression/Advice are never
        // altered), so the report's history is preserved. Serialised to the
        // client so the editor can render them below the signed report.
        public ICollection<ReportAddendum> Addenda { get; set; } = new List<ReportAddendum>();
    }

    /// <summary>
    /// Allowed values for <see cref="DiagnosticReport.Status"/>. Strings (not an
    /// enum) to match the existing status columns and to keep the DB human-readable.
    /// </summary>
    public static class ReportStatuses
    {
        public const string Draft       = "Draft";       // editable, autosaved
        public const string Preliminary = "Preliminary"; // "wet read" — signed but not final; still editable into Final
        public const string Final       = "Final";       // identity-bound signature; content locked server-side
        public const string Addended    = "Addended";    // Final + ≥1 immutable addendum; still locked

        public static bool IsLocked(string? status) =>
            status == Final || status == Addended;

        public static bool IsSigned(string? status) =>
            status == Preliminary || status == Final || status == Addended;
    }

    /// <summary>
    /// A formal addendum appended to a finalised report. Append-only: never
    /// updated or deleted, and the parent report's signed content is left intact.
    /// </summary>
    public class ReportAddendum : BaseEntity, IHospitalContext
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ReportId { get; set; }
        public Guid HospitalId { get; set; }

        // Identity-bound author of the addendum (authenticated user), with
        // name/credential snapshots captured at signing time.
        public Guid? AuthorUserId { get; set; }
        public string AuthorName { get; set; } = string.Empty;
        public string? AuthorCredentials { get; set; }

        public string Text { get; set; } = string.Empty;   // addendum body (plain text / light HTML)
        public string? ContentHash { get; set; }            // SHA-256 of Text at signing
        public DateTime SignedAt { get; set; } = DateTime.UtcNow;
        public int SortOrder { get; set; } = 0;             // append order (1, 2, 3 …)
        public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        [JsonIgnore]
        public DiagnosticReport Report { get; set; } = null!;
    }

    /// <summary>
    /// Append-only, tamper-evident audit trail for the report sign-off lifecycle.
    /// Each event chains to the previous one for this report via PreviousHash, so
    /// a deleted or altered row breaks the chain. Never updated or deleted.
    /// </summary>
    public class ReportAuditEvent : BaseEntity, IHospitalContext
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ReportId { get; set; }
        public Guid HospitalId { get; set; }

        // SignedPreliminary | SignedFinal | AddendumAdded (see ReportAuditEventTypes).
        public string EventType { get; set; } = string.Empty;

        public Guid? ActorUserId { get; set; }
        public string ActorName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // ContentHash = SHA-256 of the report content at the moment of the event.
        // PreviousHash = the ContentHash of the immediately prior audit row for
        // this report (NULL for the first), forming a verifiable chain.
        public string? ContentHash { get; set; }
        public string? PreviousHash { get; set; }
        public string? Details { get; set; }   // optional JSON/notes
    }

    public static class ReportAuditEventTypes
    {
        public const string SignedPreliminary = "SignedPreliminary";
        public const string SignedFinal       = "SignedFinal";
        public const string AddendumAdded      = "AddendumAdded";
    }

    public class DiagnosticReportField : BaseEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ReportId { get; set; }
        public string? SectionName { get; set; }
        public string FieldName { get; set; } = string.Empty;
        public string FieldValue { get; set; } = string.Empty;
        public int? SortOrder { get; set; } = 0;
        public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        [JsonIgnore]
        public DiagnosticReport Report { get; set; } = null!;
    }
}
