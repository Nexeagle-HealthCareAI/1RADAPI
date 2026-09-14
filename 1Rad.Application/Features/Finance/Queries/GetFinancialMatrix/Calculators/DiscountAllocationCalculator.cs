namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class DiscountAllocationCalculator : IDiscountAllocationCalculator
{
    public DiscountDistributionDto Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices)
    {
        var centreDisc = activeInvoices.Sum(i => i.CentreDiscount);
        var referrerDisc = activeInvoices.Sum(i => i.ReferrerDiscount);
        var institutionalDisc = activeInvoices.Sum(i => i.InstitutionalDeduction);
        var totalDisc = activeInvoices.Sum(i => i.DiscountAmount);
        var otherDisc = Math.Max(0m, totalDisc - (centreDisc + referrerDisc + institutionalDisc));

        return new DiscountDistributionDto
        {
            Centre = centreDisc,
            Referrer = referrerDisc,
            Institutional = institutionalDisc,
            Other = otherDisc
        };
    }
}
