using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// One row per hospital. Stores the configured leave types + annual quotas as
/// a JSON array. Schema of each entry:
/// { "id": "sick", "name": "Sick Leave", "annualQuota": 6, "isPaid": true, "color": "#dc2626" }
/// </summary>
public class HospitalLeavePolicy : BaseEntity, IHospitalContext
{
    public Guid PolicyId { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }

    /// <summary>JSON array of LeaveType objects.</summary>
    public string LeaveTypesJson { get; set; } = "[]";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }

    // Navigation
    public Hospital Hospital { get; set; } = null!;
}
