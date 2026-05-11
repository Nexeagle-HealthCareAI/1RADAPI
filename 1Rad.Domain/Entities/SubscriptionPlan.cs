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
}
