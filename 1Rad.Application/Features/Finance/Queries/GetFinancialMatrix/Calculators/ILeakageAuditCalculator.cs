namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>Per-referring-doctor discount approval totals, for the concession/margin auditor view.</summary>
public interface ILeakageAuditCalculator
{
    List<DiscountLeakageAuditorDto> Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices);
}
