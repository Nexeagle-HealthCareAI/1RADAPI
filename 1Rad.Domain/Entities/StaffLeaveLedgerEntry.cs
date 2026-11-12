using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// A leave credit or correction that is not leave taken (migration 121):
/// OPENING (balance brought in for existing staff), ADJUST (manual correction,
/// + or −) and CARRY_IN (days carried from the previous year at year end).
/// Leave taken comes from StaffLeaveRequests; the yearly quota from the policy.
/// </summary>
public class StaffLeaveLedgerEntry : IHospitalContext
{
    public const string Opening = "OPENING";
    public const string Adjust = "ADJUST";
    public const string CarryIn = "CARRY_IN";

    public Guid EntryId { get; set; } = Guid.NewGuid();
    public Guid StaffId { get; set; }
    public Guid HospitalId { get; set; }
    public int LeaveYear { get; set; }
    public string LeaveType { get; set; } = string.Empty;
    public string Kind { get; set; } = Adjust;
    public decimal Days { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }

    public StaffMember StaffMember { get; set; } = null!;
}
