using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class ReferralCommission : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    // Identity Parameters
    public Guid ReferrerId { get; set; }
    public string ReferrerName { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public Guid? AppointmentId { get; set; }

    
    // Financial Quantum
    public decimal CommissionAmount { get; set; }
    public decimal AccumulatedTotal { get; set; }


    
    // Transactional Metadata
    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "UNPAID"; // UNPAID, PAID, Cancelled
    public DateTime? PaymentDate { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? Remarks { get; set; }
    
    // Multi-Facility Context
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Referrer Referrer { get; set; } = null!;
    public Hospital Hospital { get; set; } = null!;
}
