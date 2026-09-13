namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>
/// Sums the real deduction vectors recorded on each invoice (Centre/Referrer/
/// Institutional), with any residual DiscountAmount not covered by those
/// three vectors reported as "Other".
/// </summary>
public interface IDiscountAllocationCalculator
{
    DiscountDistributionDto Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices);
}
