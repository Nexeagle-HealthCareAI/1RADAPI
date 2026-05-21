using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// A monthly salary payout for a staff member. Captures the structure that
/// was active at disbursal time, the LWP-adjusted pay, and the payment metadata.
/// </summary>
public class SalaryDisbursement : BaseEntity, IHospitalContext
{
    public Guid DisbursementId { get; set; } = Guid.NewGuid();
    public Guid StaffId { get; set; }
    public Guid HospitalId { get; set; }
    public Guid? RevisionId { get; set; }

    /// <summary>"YYYY-MM" — the pay-period month.</summary>
    public string Month { get; set; } = string.Empty;

    // Pay snapshot (LWP applied)
    public decimal GrossPay { get; set; }
    public decimal NetPay { get; set; }

    // Structure snapshot (before LWP)
    public decimal StructureGross { get; set; }
    public decimal StructureNet { get; set; }

    // LWP detail
    public decimal LwpDays { get; set; }
    public decimal LwpDeduction { get; set; }
    public decimal PerDayRate { get; set; }
    public int PaidLeaveInMonth { get; set; }
    public int LwpLeaveInMonth { get; set; }

    /// <summary>JSON snapshot of the attendance counts (present, absent, halfday, late, leave).</summary>
    public string? AttendanceJson { get; set; }

    // Payment metadata
    /// <summary>bank | cash | upi | cheque</summary>
    public string PaymentMode { get; set; } = "bank";
    public string? Reference { get; set; }
    public DateOnly PaidOnDate { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }

    // Navigation
    public StaffMember StaffMember { get; set; } = null!;
    public SalaryRevision? Revision { get; set; }
}
