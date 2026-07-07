using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Patient : BaseEntity, IHospitalContext
{
    public Guid PatientId { get; set; } = Guid.NewGuid();
    public string? FullName { get; set; } = string.Empty;
    public string? Mobile { get; set; } = string.Empty;
    public string? Age { get; set; } = string.Empty;
    public string? Gender { get; set; } = string.Empty; // Male, Female, Other
    public string? Village { get; set; } = string.Empty;
    public string? Block { get; set; } = string.Empty;
    public string? District { get; set; } = string.Empty;
    public string? Address { get; set; } = string.Empty;
    public string? PatientIdentifier { get; set; } = string.Empty; // MRN
    public string? SourceOfInfo { get; set; } = string.Empty;

    // Normalised name (lowercased, punctuation/honorifics stripped) used by the
    // duplicate-detection safety net to collapse casing/spacing/honorific
    // variants of the same name. NULL on legacy rows until backfilled.
    public string? NameNormalized { get; set; }
    public Guid? ReferrerId { get; set; }
    public Guid HospitalId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Sync engine fields (Phase B1 Slice 2). UpdatedAt is stamped by the
    // SaveChanges hook in ApplicationDbContext on every write; DeletedAt is
    // a soft-delete tombstone so the client can purge its local cache.
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public Referrer? Referrer { get; set; }
}
