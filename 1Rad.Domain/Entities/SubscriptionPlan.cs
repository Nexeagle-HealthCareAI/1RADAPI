using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class SubscriptionPlan : BaseEntity
{
    public Guid PlanId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty; // Monthly, Yearly
    public decimal Price { get; set; }
    public int DurationInDays { get; set; }
    public decimal DiscountPercentage { get; set; }
    public decimal PerAdditionalDoctorPrice { get; set; } = 1000;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Product edition (SKU) this plan sells. Display label; the authoritative
    // entitlement is Modules below. "RIS" | "RIS+PACS" | "PACS".
    public string Edition { get; set; } = "RIS+PACS";

    // Modules a subscription on this plan enables (CSV "RIS,PACS"). Copied onto
    // the HospitalSubscription at activation.
    public string Modules { get; set; } = Constants.ModuleConstants.DefaultModules;

    // PACS storage allowance included in the base Price. NULL = no DICOM storage
    // (RIS-only). Overage beyond this is billed at PerGbOveragePrice.
    public int? IncludedStorageGb { get; set; }

    // Price per GB of PACS storage used beyond IncludedStorageGb, assessed at
    // each billing event (renew/upgrade). 0 for RIS-only.
    public decimal PerGbOveragePrice { get; set; }
}
