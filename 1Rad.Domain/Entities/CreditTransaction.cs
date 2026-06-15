using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

// A single movement in a patient's credit wallet. The wallet balance is the
// running sum: + ADVANCE (overpayment parked at collection), − APPLIED (credit
// used against a later invoice), − REFUND (cash returned). Hospital-scoped via
// the global query filter (IHospitalContext).
public class CreditTransaction : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }

    public Guid PatientId { get; set; }
    public string PatientName { get; set; } = string.Empty; // denormalised for the advances/refunds list

    // ADVANCE (credit in) | APPLIED (credit → invoice) | REFUND (cash out)
    public string Type { get; set; } = "ADVANCE";
    public decimal Amount { get; set; } // always positive; the sign is implied by Type

    // Source invoice (ADVANCE) / target invoice (APPLIED) / null (cash REFUND).
    public Guid? InvoiceId { get; set; }
    public string? InvoiceDisplayId { get; set; }

    public string? PaymentMethod { get; set; } // CASH / UPI / CARD — how it was received or returned
    public string? Remarks { get; set; }
    public Guid? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
}
