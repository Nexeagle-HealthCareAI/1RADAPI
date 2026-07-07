using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Referrer : BaseEntity, IHospitalContext
{
    public Guid ReferrerId { get; set; } = Guid.NewGuid();
    public string? Name { get; set; } = string.Empty;
    public string? Contact { get; set; } = string.Empty;
    public string? Address { get; set; } = string.Empty;
    public Guid HospitalId { get; set; }

    // Payee-first model. The referrer record IS the payee (who collects the
    // cut). IsDoctor distinguishes the two kinds:
    //   • true  → a doctor who refers and collects for themselves; the
    //             Email/Specialty/Degree profile below applies.
    //   • false → another person (agent) who collects on a doctor's behalf;
    //             SupportedByDoctor names that doctor.
    public bool IsDoctor { get; set; } = true;
    public string? SupportedByDoctor { get; set; }

    // Optional referring-doctor profile (used when IsDoctor). All nullable —
    // a referrer may be a walk-in physician we only know by name.
    public string? Email { get; set; }
    public string? Specialty { get; set; }
    public string? Degree { get; set; }

    // Sync engine fields (Phase B3 Slice 4).
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    // Merging Support
    public Guid? MergedIntoId { get; set; }
    public Referrer? MergedInto { get; set; }
    public ICollection<Referrer> MergedDuplicates { get; set; } = new List<Referrer>();

    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public ICollection<Patient> Patients { get; set; } = new List<Patient>();
    public ICollection<ReferralCommission> Commissions { get; set; } = new List<ReferralCommission>();
}
