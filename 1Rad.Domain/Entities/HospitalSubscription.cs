using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class HospitalSubscription : BaseEntity
{
    public Guid SubscriptionId { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public Guid? PlanId { get; set; }
    public DateTime StartDate { get; set; } = DateTime.UtcNow;
    public DateTime EndDate { get; set; }
    public bool IsTrial { get; set; }
    public string Status { get; set; } = "Active"; // Active, Expired, Cancelled
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Hospital Hospital { get; set; } = null!;
    public SubscriptionPlan? Plan { get; set; }
}
