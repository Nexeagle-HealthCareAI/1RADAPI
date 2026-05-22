using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class HospitalSubscription : BaseEntity
{
    public Guid SubscriptionId { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public Guid? PlanId { get; set; }
    public string BillingCycle { get; set; } = "Trial"; // Trial | Monthly | Yearly
    public DateTime StartDate { get; set; } = DateTime.UtcNow;
    public DateTime EndDate { get; set; }
    public bool IsTrial { get; set; }
    public string Status { get; set; } = "Active"; // Active | Expiring | Expired | Locked
    public bool IsLocked { get; set; } = false;
    public DateTime? LockedAt { get; set; }
    public string? LockReason { get; set; } // GracePeriodExpired | PaymentOverdue
    public DateTime? NotificationSentAt { get; set; }
    public Guid? ActivatedByUserId { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public SubscriptionPlan? Plan { get; set; }
}
