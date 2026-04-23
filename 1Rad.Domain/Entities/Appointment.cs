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
    public string? Doctor { get; set; }
    public string? Status { get; set; } // BOOKED, ARRIVED, IN_PROGRESS, COMPLETED, CANCELLED
    
    public string? ReferredBy { get; set; }
    public string? ReferredContact { get; set; }
    public string? Notes { get; set; }
    public string? TechnicianComments { get; set; }
    public Guid? TechnicianId { get; set; }
    public DateTime? ScannedAt { get; set; }
    
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public Patient Patient { get; set; } = null!;
    public ICollection<StudyAsset> StudyAssets { get; set; } = new List<StudyAsset>();
}
