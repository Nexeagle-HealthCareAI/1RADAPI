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
