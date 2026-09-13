namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class ModalityRevenueCalculator : IModalityRevenueCalculator
{
    public List<ModalityRevenueDto> Calculate(IReadOnlyList<ServiceLineRow> serviceLines, decimal totalLifetimeInvoiced)
    {
        return serviceLines
            .GroupBy(x => x.Modality)
            .Select(g => new ModalityRevenueDto
            {
                Modality = g.Key,
                RangeRevenue = g.Sum(x => x.Total),
                ContributionPercentage = totalLifetimeInvoiced > 0
                    ? (int)(g.Sum(x => x.Total) / totalLifetimeInvoiced * 100)
                    : 0
            })
            .OrderByDescending(x => x.RangeRevenue)
            .ToList();
    }
}
