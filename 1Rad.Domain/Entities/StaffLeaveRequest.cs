using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// A leave application from a staff member.
/// Status values: pending | approved | rejected
/// </summary>
public class StaffLeaveRequest : BaseEntity, IHospitalContext
{
    public Guid LeaveRequestId { get; set; } = Guid.NewGuid();
    public Guid StaffId { get; set; }
    public Guid HospitalId { get; set; }

    public string LeaveType { get; set; } = string.Empty;

    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public int Days { get; set; }

    public string? Reason { get; set; }

    /// <summary>pending | approved | rejected</summary>
    public string Status { get; set; } = "pending";

    public DateTime AppliedOn { get; set; } = DateTime.UtcNow;

    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }

    // Navigation
    public StaffMember StaffMember { get; set; } = null!;
}
