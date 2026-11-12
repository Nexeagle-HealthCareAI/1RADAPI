using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// One payout to a referral partner (review R10, R12; migration 106): the money that
/// actually changed hands, with the tax deducted at source. Each commission row paid
/// in it points here (ReferralCommission.PayoutId).
///   Gross − DeficitRecovered = Taxable; Taxable − TdsAmount = Net (cash handed over).
/// </summary>
public class ReferralPayout : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public string PayoutNumber { get; set; } = string.Empty;   // RPO2627-00001
    public Guid ReferrerId { get; set; }                        // the partner (merge root)
    public string PayeeName { get; set; } = string.Empty;
    public string? PayeePan { get; set; }
    public string FinancialYear { get; set; } = string.Empty;   // 2026-27
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;
    public string PaidBy { get; set; } = string.Empty;
    public Guid? CreatedByUserId { get; set; }
    public string Mode { get; set; } = "CASH";                  // CASH / UPI / BANK / CHEQUE
    public string? Reference { get; set; }                      // UTR / cheque no.
    public decimal Gross { get; set; }
    public decimal DeficitRecovered { get; set; }
    public string TdsCategory { get; set; } = "NONE";           // COMMISSION / PROFESSIONAL / NONE
    public decimal TdsRate { get; set; }                        // percent
    public decimal TdsAmount { get; set; }
    public decimal Net { get; set; }
    public string? TdsNote { get; set; }
}
