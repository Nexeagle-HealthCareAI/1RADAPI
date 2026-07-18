using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class InvoiceExtraCharge : BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Invoice Invoice { get; set; } = null!;
}
