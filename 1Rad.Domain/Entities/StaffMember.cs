using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// An HR record for a staff member at a hospital.
/// Does NOT require a board login account — board access is optional
/// and is granted separately via <see cref="BoardAccessUserId"/>.
/// </summary>
public class StaffMember : BaseEntity, IHospitalContext
{
    public Guid StaffId { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Mobile { get; set; }

    public string? Designation { get; set; }
    public string? Department { get; set; }
    public string EmploymentType { get; set; } = "Full-Time"; // Full-Time | Part-Time | Consultant | Contract

    public string? Specialization { get; set; }
    public string? Degree { get; set; }
    public string? LicenseNo { get; set; }

    public DateOnly? JoiningDate { get; set; }
    public string Status { get; set; } = "Active"; // Active | Inactive

    /// <summary>Populated once board access is granted via /staff/{id}/grant-access.</summary>
    public Guid? BoardAccessUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public User? BoardAccessUser { get; set; }
    public ICollection<StaffMemberRole> Roles { get; set; } = new List<StaffMemberRole>();
    public ICollection<StaffDocument> Documents { get; set; } = new List<StaffDocument>();
}
