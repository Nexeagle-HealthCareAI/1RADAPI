using System.ComponentModel.DataAnnotations.Schema;
using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Invoice : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string InvoiceId { get; set; } = string.Empty; // e.g., INV-2024-001
    
    public Guid? AppointmentId { get; set; }
    public Guid PatientId { get; set; }
    
    public string? PatientName { get; set; } = string.Empty; // Denormalized for financial records
    
    public decimal GrossAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; } // Net Amount
    public decimal PaidAmount { get; set; }
    public decimal BalanceAmount => TotalAmount - PaidAmount;
    
    public string Status { get; set; } = "PENDING"; // PENDING, PARTIAL, PAID, CANCELLED

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ServiceDate { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }

    // Sync engine fields (Phase B3 Slice 1). UpdatedAt is stamped by the
    // SaveChanges hook in ApplicationDbContext on every write; DeletedAt
    // is the soft-delete tombstone for client cache purges.
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
    
    public decimal ReferralCutValue { get; set; } = 0;
    
    // Triple-Vector Deduction Tracking
    public decimal CentreDiscount { get; set; } = 0;
    public decimal ReferrerDiscount { get; set; } = 0;
    public decimal InstitutionalDeduction { get; set; } = 0;

    // Surcharges (e.g. Night Charges)
    public decimal AdditionalCharges { get; set; } = 0;
    public string? AdditionalChargesReason { get; set; }


    // Free test (Scenario 07): the bill is kept (gross recorded) but payable,
    // income and commission are all zeroed. This flag lets reports tell a genuine
    // free test apart from an ordinary 100% centre discount.
    public bool IsFree { get; set; } = false;


    
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public Patient Patient { get; set; } = null!;
    public Appointment? Appointment { get; set; }
    public ICollection<InvoiceItem> Items { get; set; } = new List<InvoiceItem>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public ICollection<InvoiceExtraCharge> ExtraCharges { get; set; } = new List<InvoiceExtraCharge>();
}
