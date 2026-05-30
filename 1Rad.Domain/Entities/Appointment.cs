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

    public int? DailyTokenNumber { get; set; }  // Persisted token, assigned atomically on creation
    
    public string? DelayReason { get; set; }
    public string ReportProgressStatus { get; set; } = "NOT_STARTED";
    
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public Patient Patient { get; set; } = null!;
    public ICollection<StudyAsset> StudyAssets { get; set; } = new List<StudyAsset>();
}
