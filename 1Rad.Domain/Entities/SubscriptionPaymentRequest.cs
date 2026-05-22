using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class SubscriptionPaymentRequest : BaseEntity
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public string PlanName { get; set; } = string.Empty;          // Monthly | Yearly
    public string BillingCycle { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string PayerName { get; set; } = string.Empty;
    public string PayerContact { get; set; } = string.Empty;
    public string TransactionReference { get; set; } = string.Empty; // UTR / Cheque / Bank Ref
    public string PaymentMode { get; set; } = string.Empty;      // UPI | Bank Transfer | NEFT | Cheque
    public DateTime PaidAt { get; set; }
    public string Status { get; set; } = "Pending";              // Pending | Approved | Rejected
    public string? ReviewNote { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    // Payment gateway hooks (nullable — for future Razorpay/Stripe)
    public string? PaymentGatewayOrderId { get; set; }
    public string? PaymentGatewayResponse { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Hospital Hospital { get; set; } = null!;
}
