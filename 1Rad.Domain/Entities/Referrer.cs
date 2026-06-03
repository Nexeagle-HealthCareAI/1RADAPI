using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Referrer : BaseEntity, IHospitalContext
{
    public Guid ReferrerId { get; set; } = Guid.NewGuid();
    public string? Name { get; set; } = string.Empty;
    public string? Contact { get; set; } = string.Empty;
    public string? Address { get; set; } = string.Empty;
    public Guid HospitalId { get; set; }

    // Optional referring-doctor profile. All nullable — a referrer may be a
    // walk-in physician we only know by name.
    public string? Email { get; set; }
    public string? Specialty { get; set; }
    public string? Degree { get; set; }

    // Sync engine fields (Phase B3 Slice 4).
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public ICollection<Patient> Patients { get; set; } = new List<Patient>();
    public ICollection<ReferralCommission> Commissions { get; set; } = new List<ReferralCommission>();
}
