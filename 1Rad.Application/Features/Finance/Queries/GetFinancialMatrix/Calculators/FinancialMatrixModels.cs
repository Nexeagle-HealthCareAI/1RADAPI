namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

// Plain, DB-agnostic row shapes that every calculator below consumes. The
// handler is the only place that talks to the DbContext — it hydrates these
// rows once, then every calculator is a pure function of already-in-memory
// data. That's what makes each one unit-testable with a hand-built list and
// no DbContext/InMemory provider at all.

public record InvoiceMatrixRow
{
    public Guid Id { get; init; }
    public string InvoiceId { get; init; } = string.Empty; // display id, e.g. INV-...
    public Guid PatientId { get; init; }
    public string? PatientName { get; init; }
    public decimal GrossAmount { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal PaidAmount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ServiceDate { get; init; }
    public decimal ReferralCutValue { get; init; }
    public decimal CentreDiscount { get; init; }
    public decimal ReferrerDiscount { get; init; }
    public decimal InstitutionalDeduction { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Modality { get; init; } = "GENERAL";
    public string Service { get; init; } = "OTHER";
    public bool HasReferrer { get; init; }
    public string? ReferredBy { get; init; }
}

public record ExpenseMatrixRow
{
    public decimal Amount { get; init; }
    public decimal TaxAmount { get; init; }
    public DateTime TransactionDate { get; init; }
    public string? Category { get; init; }
    public string? CostCenter { get; init; }
    public string? Description { get; init; }
}

public record CommissionMatrixRow
{
    public Guid ReferrerId { get; init; }
    public string? ReferrerName { get; init; }
    public decimal CommissionAmount { get; init; }
    public DateTime TransactionDate { get; init; }
    public string? Status { get; init; }
}

public record PaymentMatrixRow
{
    public decimal Amount { get; init; }
    public string? PaymentMethod { get; init; }
}

// One row per billed service line (per-line, not per-invoice — see the
// original handler's comment on why: an invoice's own denormalised
// Modality/Service reflects only the FIRST service on a multi-service
// visit, so grouping by service LINE is what makes every scan on a
// multi-service visit count under its own name).
public record ServiceLineRow
{
    public string Modality { get; init; } = "GENERAL";
    public string ServiceName { get; init; } = "OTHER";
    public decimal Gross { get; init; }
    public decimal Total { get; init; }
    public decimal Paid { get; init; }
    public decimal ReferralCut { get; init; }
}
