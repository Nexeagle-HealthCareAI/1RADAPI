using System.ComponentModel.DataAnnotations.Schema;
using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Invoice : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string InvoiceId { get; set; } = string.Empty; // e.g., INV-2024-001
    
    public Guid? AppointmentId { get; set; }
    public Guid PatientId { get; set; }
    
    [NotMapped]
    public string PatientName { get; set; } = string.Empty; // Denormalized for financial records
    
    public decimal GrossAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; } // Net Amount
    public decimal PaidAmount { get; set; }
    public decimal BalanceAmount => TotalAmount - PaidAmount;
    
    public string Status { get; set; } = "PENDING"; // PENDING, PARTIAL, PAID, CANCELLED

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }
    
    public decimal ReferralCutValue { get; set; } = 0;
    
    // Triple-Vector Deduction Tracking
    public decimal CentreDiscount { get; set; } = 0;
    public decimal ReferrerDiscount { get; set; } = 0;
    public decimal InstitutionalDeduction { get; set; } = 0;


    
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Hospital Hospital { get; set; } = null!;
    public Patient Patient { get; set; } = null!;
    public Appointment? Appointment { get; set; }
    public ICollection<InvoiceItem> Items { get; set; } = new List<InvoiceItem>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
