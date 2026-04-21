using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class InvoiceItem : BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal SubTotal => Amount * Quantity;
    
    // Navigation
    public Invoice Invoice { get; set; } = null!;
}
