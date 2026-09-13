namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class AgingAnalysisCalculator : IAgingAnalysisCalculator
{
    public AgingAnalysisDto Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices, DateTime referenceDate)
    {
        var outstandingInvoices = activeInvoices
            .Where(i => i.PaidAmount < i.TotalAmount)
            .Select(i => new
            {
                Outstanding = i.TotalAmount - i.PaidAmount,
                AgeInDays = (referenceDate - i.ServiceDate).Days
            })
            .ToList();

        return new AgingAnalysisDto
        {
            Bucket0To30 = outstandingInvoices.Where(x => x.AgeInDays <= 30).Sum(x => x.Outstanding),
            Bucket31To60 = outstandingInvoices.Where(x => x.AgeInDays > 30 && x.AgeInDays <= 60).Sum(x => x.Outstanding),
            Bucket61To90 = outstandingInvoices.Where(x => x.AgeInDays > 60 && x.AgeInDays <= 90).Sum(x => x.Outstanding),
            Bucket91Plus = outstandingInvoices.Where(x => x.AgeInDays > 90).Sum(x => x.Outstanding)
        };
    }
}
