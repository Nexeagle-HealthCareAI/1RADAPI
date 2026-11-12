using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Payment : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = "CASH"; // CASH, UPI, CARD, ADVANCE (wallet)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Append-only ledger (migration 102). A PAYMENT is positive; a REFUND is its own
    // negative row dated the day the money went back. Rows are never edited or deleted.
    public string Kind { get; set; } = PaymentKinds.Payment;
    public string? Reason { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? RefundOfPaymentId { get; set; }

    // Migration 116. UPI / card / bank reference typed at the counter, and the receipt
    // number ("RCP-2627-00001") shared by every row of one collection (a split cash +
    // UPI payment is one receipt with two rows).
    public string? Reference { get; set; }
    public string? ReceiptNumber { get; set; }
    
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Invoice Invoice { get; set; } = null!;
    public Hospital Hospital { get; set; } = null!;
}

public static class PaymentKinds
{
    public const string Payment = "PAYMENT";
    public const string Refund = "REFUND";
}
