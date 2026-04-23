using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Appointment : BaseEntity, IHospitalContext
{
    public Guid AppointmentId { get; set; } = Guid.NewGuid();
    public string DisplayId { get; set; } = string.Empty; // e.g., APP-101
    public Guid PatientId { get; set; }
    public string PatientName { get; set; } = string.Empty; // Denormalized for quick tactical display
    public string Mobile { get; set; } = string.Empty;
    
    public string Service { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty; // X-RAY, MRI, etc.
    public DateTime DateTime { get; set; }
    public string Type { get; set; } = "BOOKED"; // BOOKED, EMERGENCY, ROUTINE
    public string Doctor { get; set; } = string.Empty;
    public string Status { get; set; } = "BOOKED"; // BOOKED, ARRIVED, IN_PROGRESS, COMPLETED, CANCELLED
    
    public string ReferredBy { get; set; } = string.Empty;
    public string ReferredContact { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string TechnicianComments { get; set; } = string.Empty;
    public Guid? TechnicianId { get; set; }
    public DateTime? ScannedAt { get; set; }
    
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public Patient Patient { get; set; } = null!;
    public ICollection<StudyAsset> StudyAssets { get; set; } = new List<StudyAsset>();
}
