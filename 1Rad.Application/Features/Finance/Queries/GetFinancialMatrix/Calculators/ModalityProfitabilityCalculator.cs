namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class ModalityProfitabilityCalculator : IModalityProfitabilityCalculator
{
    private readonly IModalityExpenseAllocator _expenseAllocator;

    public ModalityProfitabilityCalculator(IModalityExpenseAllocator expenseAllocator)
    {
        _expenseAllocator = expenseAllocator;
    }

    public List<ModalityProfitabilityDto> Calculate(IReadOnlyList<ServiceLineRow> serviceLines, IReadOnlyList<ExpenseMatrixRow> expenseData)
    {
        var totalScans = serviceLines.Count;
        var allocation = _expenseAllocator.Allocate(expenseData);

        return serviceLines
            .GroupBy(x => x.Modality)
            .Select(g =>
            {
                var mod = g.Key;
                var count = g.Count();
                var gross = g.Sum(x => x.Gross);
                var cut = g.Sum(x => x.ReferralCut);
                var net = gross - cut;
                var paid = g.Sum(x => x.Paid);

                var directCost = allocation.DirectByModality.GetValueOrDefault(mod, 0m);
                var proportionalShare = totalScans > 0 ? (decimal)count / totalScans * allocation.GeneralRadiologyOverhead : 0m;
                var operatingCost = directCost + proportionalShare;

                var netOpProfit = net - operatingCost;
                var opMarginPct = net > 0 ? (double)Math.Round((netOpProfit / net) * 100, 1) : 0;
                var roi = operatingCost > 0 ? (double)Math.Round(gross / operatingCost, 1) : 0;

                var avgNetYield = count > 0 ? net / count : 0m;
                var breakEven = avgNetYield > 0 ? Math.Round(operatingCost / avgNetYield, 1) : 0m;

                var services = g.GroupBy(x => x.ServiceName)
                    .Select(sg =>
                    {
                        var svcCount = sg.Count();
                        var svcGross = sg.Sum(x => x.Gross);
                        var svcCut = sg.Sum(x => x.ReferralCut);
                        var svcNet = svcGross - svcCut;
                        var svcPaid = sg.Sum(x => x.Paid);

                        return new ServiceProfitabilityDto
                        {
                            ServiceName = sg.Key,
                            ScanCount = svcCount,
                            GrossRevenue = svcGross,
                            ReferralCut = svcCut,
                            NetRevenue = svcNet,
                            MarginPercentage = svcGross > 0 ? (double)Math.Round((svcNet / svcGross) * 100, 1) : 0,
                            CollectionEfficiency = svcGross > 0 ? (double)Math.Round((svcPaid / svcGross) * 100, 1) : 0
                        };
                    })
                    .OrderByDescending(s => s.GrossRevenue)
                    .ToList();

                return new ModalityProfitabilityDto
                {
                    Modality = mod,
                    ScanCount = count,
                    GrossRevenue = gross,
                    ReferralCut = cut,
                    NetRevenue = net,
                    MarginPercentage = gross > 0 ? (double)Math.Round((net / gross) * 100, 1) : 0,
                    CollectionEfficiency = gross > 0 ? (double)Math.Round((paid / gross) * 100, 1) : 0,
                    OperatingCost = operatingCost,
                    NetOperatingProfit = netOpProfit,
                    OperatingMarginPercentage = opMarginPct,
                    EquipmentRoiRatio = roi,
                    BreakEvenScansNeeded = breakEven,
                    Services = services
                };
            })
            .OrderByDescending(m => m.GrossRevenue)
            .ToList();
    }
}
