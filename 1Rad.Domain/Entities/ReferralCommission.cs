using System.ComponentModel.DataAnnotations.Schema;
using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class ReferralCommission : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    // Identity Parameters
    public Guid ReferrerId { get; set; }
    
    public string ReferrerName { get; set; } = string.Empty;
    
    public string Modality { get; set; } = string.Empty;
    
    [NotMapped]
    public string PatientName { get; set; } = string.Empty;
    
    public Guid? AppointmentId { get; set; }

    // Multi-service support (migration 57). With multi-service visits the
    // referrer earns one commission per service line, so this column
    // points at the specific AppointmentService row. NULL on legacy
    // single-service appointments — those still resolve via AppointmentId.
    public Guid? AppointmentServiceId { get; set; }

    // Financial Quantum
    public decimal CommissionAmount { get; set; }
    public decimal AccumulatedTotal { get; set; }


    
    // Transactional Metadata
    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
    public DateTime ServiceDate { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "UNPAID"; // UNPAID, PAID, Cancelled
    public DateTime? PaymentDate { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? Remarks { get; set; }

    // Who actually receives this referral payment. NULL means "pay the
    // referring doctor" (the common case); a value means the cut is owed to an
    // associated person (e.g. the doctor's agent) instead. The payout screen
    // pays whoever is named here, falling back to the referrer when null.
    public string? PayeeName { get; set; }
    public string? PayeeContact { get; set; }

    // Extended payout tracking — who sent the payment and full recipient details.
    // These are mandatory when marking a commission PAID (migration 84).
    public string? PaidBy { get; set; }          // Name of staff / user who disbursed
    public string? PayeeEmail { get; set; }
    public string? PayeeAddress { get; set; }

    // Audit trail
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    
    // Multi-Facility Context
    public Guid HospitalId { get; set; }

    // Sync engine fields (Phase B3 Slice 5).
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
    
    // Navigation
    public Referrer Referrer { get; set; } = null!;
    public Hospital Hospital { get; set; } = null!;
}
