using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Expense : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // Maintenance, Staff, Utilities, Reagents
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid HospitalId { get; set; }
    
    // Navigation
    public Hospital Hospital { get; set; } = null!;
}
