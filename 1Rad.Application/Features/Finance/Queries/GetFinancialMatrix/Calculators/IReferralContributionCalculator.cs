namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>Revenue split between referred and direct (walk-in) patients.</summary>
public interface IReferralContributionCalculator
{
    ReferralContributionDto Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices);
}
