using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Payment : BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = "CASH"; // CASH, UPI, CARD
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation
    public Invoice Invoice { get; set; } = null!;
}
