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
                // `total` is each line's post-discount billed share (Total on
                // ServiceLineRow — see the handler) — the actually-billed amount,
                // unlike `gross` which is the pre-discount list price. Net/margin/
                // efficiency/ROI are all "of what was really billed", so they use
                // `total`; `gross` stays around only as the list-price figure the
                // UI shows separately. Net used to be gross - cut, silently
                // ignoring every discount applied to the invoice.
                var total = g.Sum(x => x.Total);
                var cut = g.Sum(x => x.ReferralCut);
                var net = total - cut;
                var paid = g.Sum(x => x.Paid);

                var directCost = allocation.DirectByModality.GetValueOrDefault(mod, 0m);
                var proportionalShare = totalScans > 0 ? (decimal)count / totalScans * allocation.GeneralRadiologyOverhead : 0m;
                var operatingCost = directCost + proportionalShare;

                var netOpProfit = net - operatingCost;
                var opMarginPct = net > 0 ? (double)Math.Round((netOpProfit / net) * 100, 1) : 0;
                var roi = operatingCost > 0 ? (double)Math.Round(total / operatingCost, 1) : 0;

                var avgNetYield = count > 0 ? net / count : 0m;
                var breakEven = avgNetYield > 0 ? Math.Round(operatingCost / avgNetYield, 1) : 0m;

                var services = g.GroupBy(x => x.ServiceName)
                    .Select(sg =>
                    {
                        var svcCount = sg.Count();
                        var svcGross = sg.Sum(x => x.Gross);
                        var svcTotal = sg.Sum(x => x.Total);
                        var svcCut = sg.Sum(x => x.ReferralCut);
                        var svcNet = svcTotal - svcCut;
                        var svcPaid = sg.Sum(x => x.Paid);

                        return new ServiceProfitabilityDto
                        {
                            ServiceName = sg.Key,
                            ScanCount = svcCount,
                            GrossRevenue = svcGross,
                            ReferralCut = svcCut,
                            NetRevenue = svcNet,
                            MarginPercentage = svcTotal > 0 ? (double)Math.Round((svcNet / svcTotal) * 100, 1) : 0,
                            CollectionEfficiency = svcTotal > 0 ? (double)Math.Round((svcPaid / svcTotal) * 100, 1) : 0
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
                    MarginPercentage = total > 0 ? (double)Math.Round((net / total) * 100, 1) : 0,
                    CollectionEfficiency = total > 0 ? (double)Math.Round((paid / total) * 100, 1) : 0,
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
