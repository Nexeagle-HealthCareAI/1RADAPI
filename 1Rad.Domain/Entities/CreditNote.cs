using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// A document issued when money or value goes back on a bill after it has been
/// paid or part-paid (review B6/B10, migration 108). Raised automatically when the
/// bill is saved, so no billing path can forget it:
///
///   • CREDIT_NOTE ("CN2627-00001") — the bill's value went down after payment: a
///     concession, a service dropped, the bill made free or cancelled. Amount is the
///     reduction. GST section 34; reported in GSTR-1.
///   • REFUND_VOUCHER ("RV2627-00001") — money returned while the bill's value stays
///     the same (e.g. an over-collection handed back). GST rule 51.
///
/// RefundedAmount / RefundModes record what was actually handed back alongside it
/// (0 when the reduction only lowered what the patient still owed).
/// Rows are never edited or deleted.
/// </summary>
public class CreditNote : IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public string Kind { get; set; } = CreditNoteKinds.CreditNote;
    public string Number { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;   // "2026-27"
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }                   // the bill's CreatedAt (UTC)
    public Guid PatientId { get; set; }
    public string? PatientName { get; set; }

    public decimal Amount { get; set; }          // value reduced (CN) or money returned (RV)
    public decimal TaxableValue { get; set; }    // Amount less the GST inside it
    public decimal TaxAmount { get; set; }       // GST inside Amount (CGST + SGST)
    public decimal RefundedAmount { get; set; }
    public string? RefundModes { get; set; }     // "CASH", "ADVANCE, UPI", …

    public decimal BillTotalBefore { get; set; }
    public decimal BillTotalAfter { get; set; }
    public string Reason { get; set; } = string.Empty;

    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class CreditNoteKinds
{
    public const string CreditNote = "CREDIT_NOTE";
    public const string RefundVoucher = "REFUND_VOUCHER";
}
