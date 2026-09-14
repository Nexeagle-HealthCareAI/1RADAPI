namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class LeakageAuditCalculator : ILeakageAuditCalculator
{
    public List<DiscountLeakageAuditorDto> Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices)
    {
        return activeInvoices
            .Where(i => i.HasReferrer && !string.IsNullOrEmpty(i.ReferredBy))
            .GroupBy(i => i.ReferredBy!)
            .Select(g => new DiscountLeakageAuditorDto
            {
                DoctorName = g.Key,
                TotalDiscountApproved = g.Sum(x => x.DiscountAmount),
                TotalBilledRevenue = g.Sum(x => x.GrossAmount)
            })
            .OrderByDescending(x => x.TotalDiscountApproved)
            .ToList();
    }
}
