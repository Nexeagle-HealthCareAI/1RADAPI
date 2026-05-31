using System.ComponentModel.DataAnnotations.Schema;
using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Appointment : BaseEntity, IHospitalContext
{
    public Guid AppointmentId { get; set; } = Guid.NewGuid();
    public string? DisplayId { get; set; } // e.g., APP-101
    public Guid PatientId { get; set; }
    
    public string? PatientName { get; set; } // Denormalized for quick tactical display
    
    public string? Mobile { get; set; }
    
    public string? Service { get; set; }
    public string? Modality { get; set; } // X-RAY, MRI, etc.
    public DateTime DateTime { get; set; }
    public string? Type { get; set; } // BOOKED, EMERGENCY, ROUTINE
    // Clinical urgency, independent of Type/Status. STAT > URGENT > ROUTINE
    // drives the worklist sort order so STATs surface at the top regardless
    // of their scheduled time. Editable after booking (front desk can bump a
    // walk-in trauma to STAT). Default ROUTINE.
    public string Priority { get; set; } = "ROUTINE";
    public string? Doctor { get; set; }
    public string? Status { get; set; } // BOOKED, ARRIVED, IN_PROGRESS, COMPLETED, CANCELLED
    
    public string? ReferredBy { get; set; }
    public string? ReferredContact { get; set; }
    public string? Notes { get; set; }
    public string? TechnicianComments { get; set; }
    public Guid? TechnicianId { get; set; }
    public DateTime? ScannedAt { get; set; }

    // Turnaround-time milestones — all stored UTC, all nullable, all set only
    // on the FIRST transition into the matching status (idempotent). Drive the
    // on-premises clock and the scan→delivery interval shown on every board,
    // plus the >3h overdue notification system.
    public DateTime? ArrivedAt { get; set; }      // Status → CONFIRMED
    public DateTime? ScanStartedAt { get; set; }  // Status → IN_PROGRESS
    public DateTime? DeliveredAt { get; set; }    // ReportProgressStatus → DELIVERED

    // SLA-bell acknowledgement — set when any user clicks "Acknowledge" in the
    // bell dropdown. Silences the bell + desktop notification + row pulse for
    // this case until it's delivered. Audit trail captures who acked + when.
    public DateTime? OverdueAcknowledgedAt { get; set; }
    public Guid? OverdueAcknowledgedBy { get; set; }

    // ── Sync foundations (migration 47) ──────────────────────────────────────
    // Wall-clock UTC of the last save touching this row. The local-first
    // sync engine reads this to do delta pulls (?updatedAfter=...). EF
    // SaveChanges interceptor maintains it so individual handlers don't
    // have to remember.
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Soft-delete tombstone. NULL = live. When non-null the row exists for
    // sync delta purposes only and is hidden from normal GETs.
    public DateTime? DeletedAt { get; set; }

    public int? DailyTokenNumber { get; set; }  // Persisted token, assigned atomically on creation
    
    public string? DelayReason { get; set; }
    // Denormalised "latest comment" author + time. Kept in sync by
    // AddAppointmentCommentCommand so the worklist can render the byline
    // inline without joining Users or running a per-row subquery against
    // AppointmentComments. Stale-by-design: if the user later renames, this
    // snapshot stays — that's the right behaviour for audit.
    public string? LatestCommentAuthorName { get; set; }
    public DateTime? LatestCommentAt { get; set; }
    public string ReportProgressStatus { get; set; } = "NOT_STARTED";
    
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public Patient Patient { get; set; } = null!;
    public ICollection<StudyAsset> StudyAssets { get; set; } = new List<StudyAsset>();
}
