using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Expense : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // Maintenance, Staff, Utilities, Reagents
    public decimal Amount { get; set; }
    public decimal TaxAmount { get; set; }
    public string? PaymentMode { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? VendorName { get; set; }
    public string? CostCenter { get; set; }
    public string Status { get; set; } = "Paid";
    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Sync engine fields (Phase B3 Slice 3). UpdatedAt is stamped by the
    // SaveChanges hook in ApplicationDbContext on every write.
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    public Guid HospitalId { get; set; }

    /// <summary>When this expense was auto-generated from a salary disbursement, FK to it. Null for manual expenses.</summary>
    public Guid? LinkedDisbursementId { get; set; }

    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public SalaryDisbursement? LinkedDisbursement { get; set; }
}
