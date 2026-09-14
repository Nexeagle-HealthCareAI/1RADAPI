namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class ReferralContributionCalculator : IReferralContributionCalculator
{
    public ReferralContributionDto Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices)
    {
        var totalNet = activeInvoices.Sum(i => i.TotalAmount);
        var referredInvoices = activeInvoices.Where(i => i.HasReferrer).ToList();
        var directInvoices = activeInvoices.Where(i => !i.HasReferrer).ToList();
        var referredRevenue = referredInvoices.Sum(i => i.TotalAmount);

        return new ReferralContributionDto
        {
            ReferredRevenue = referredRevenue,
            DirectRevenue = directInvoices.Sum(i => i.TotalAmount),
            ReferralRatio = totalNet > 0 ? (double)Math.Round((referredRevenue / totalNet) * 100, 1) : 0,
            ReferredScansCount = referredInvoices.Count,
            DirectScansCount = directInvoices.Count
        };
    }
}
