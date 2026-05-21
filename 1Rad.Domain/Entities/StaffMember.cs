using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// An HR record for a staff member at a hospital.
/// Board login access is managed separately via the Users system.
/// </summary>
public class StaffMember : BaseEntity, IHospitalContext
{
    public Guid StaffId { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }

    /// <summary>Human-readable, per-hospital employee code (e.g. "EMP-0001"). Assigned on create.</summary>
    public string? EmployeeCode { get; set; }

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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public ICollection<StaffMemberRole> Roles { get; set; } = new List<StaffMemberRole>();
    public ICollection<StaffDocument> Documents { get; set; } = new List<StaffDocument>();
    public ICollection<SalaryRevision> SalaryRevisions { get; set; } = new List<SalaryRevision>();
    public ICollection<SalaryDisbursement> SalaryDisbursements { get; set; } = new List<SalaryDisbursement>();
}
