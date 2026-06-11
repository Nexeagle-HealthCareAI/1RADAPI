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

    // Product composition this center bought, comma-separated ("RIS,PACS",
    // "RIS", "PACS"). Lives on the subscription (not the plan) because plans
    // are pricing tiers (Monthly/Yearly) and centers in one group can run
    // different SKUs. Legacy rows are backfilled to the full product.
    public string Modules { get; set; } = Constants.ModuleConstants.DefaultModules;

    // Cloud PACS downgrade lifecycle. Stamped when PACS is removed from
    // Modules; cleared when it's added back. While set and within the grace
    // window the center keeps READ-ONLY access to its studies (view/browse/
    // export/delete) but cannot ingest or report; after the window an
    // auto-delete job removes the studies. NULL = PACS active or never had it.
    public DateTime? PacsRemovedAt { get; set; }

    // Tier caps + billing mode, copied from the plan at activation so
    // enforcement (user/site limits) and PAYG billing are single-row reads.
    public string BillingMode { get; set; } = "Subscription"; // Subscription | PerStudy
    public decimal PerStudyPrice { get; set; }                // PAYG rate per finalized report
    public int? MaxUsers { get; set; }                        // NULL = unlimited
    public int? MaxSites { get; set; }                        // NULL = unlimited

    // PACS storage allowance in GB for this center. NULL = unmetered (legacy
    // and RIS-only subscriptions). When the hospital's persisted bytes exceed
    // this, NEW DICOM uploads are rejected (STORAGE_QUOTA_EXCEEDED) — viewing
    // existing studies is never blocked by quota.
    public int? IncludedStorageGb { get; set; }
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
