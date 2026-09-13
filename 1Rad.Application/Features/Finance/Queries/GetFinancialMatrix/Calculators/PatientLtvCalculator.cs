namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class PatientLtvCalculator : IPatientLtvCalculator
{
    public PatientLtvDto Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices, DateTime referenceDate)
    {
        var patientInvoicesGrouped = activeInvoices
            .GroupBy(i => i.PatientId)
            .Select(g => new
            {
                PatientId = g.Key,
                PatientName = g.First().PatientName,
                FirstVisit = g.Min(x => x.ServiceDate),
                Visits = g.Select(x => x.ServiceDate).ToList(),
                TotalRevenue = g.Sum(x => x.TotalAmount)
            })
            .ToList();

        var totalInvoicesCount = activeInvoices.Count;
        var totalGrossRevenue = activeInvoices.Sum(i => i.TotalAmount);
        var uniquePatientCount = patientInvoicesGrouped.Count;

        var aov = totalInvoicesCount > 0 ? totalGrossRevenue / totalInvoicesCount : 0m;
        var pf = uniquePatientCount > 0 ? (double)totalInvoicesCount / uniquePatientCount : 0;
        var pv = aov * (decimal)pf;
        var estimatedLtv = pv * 3.0m; // 3-year projected lifespan

        var highValueCount = 0; var highValueRev = 0m;
        var midValueCount = 0; var midValueRev = 0m;
        var lowValueCount = 0; var lowValueRev = 0m;

        foreach (var p in patientInvoicesGrouped)
        {
            if (p.TotalRevenue >= 15000m) { highValueCount++; highValueRev += p.TotalRevenue; }
            else if (p.TotalRevenue >= 5000m) { midValueCount++; midValueRev += p.TotalRevenue; }
            else { lowValueCount++; lowValueRev += p.TotalRevenue; }
        }

        var ltvSegments = new List<LtvSegmentDto>
        {
            new() { Tier = "High Value", PatientCount = highValueCount, TotalRevenue = highValueRev, Percentage = uniquePatientCount > 0 ? Math.Round((double)highValueCount / uniquePatientCount * 100, 1) : 0 },
            new() { Tier = "Mid Value", PatientCount = midValueCount, TotalRevenue = midValueRev, Percentage = uniquePatientCount > 0 ? Math.Round((double)midValueCount / uniquePatientCount * 100, 1) : 0 },
            new() { Tier = "Low Value", PatientCount = lowValueCount, TotalRevenue = lowValueRev, Percentage = uniquePatientCount > 0 ? Math.Round((double)lowValueCount / uniquePatientCount * 100, 1) : 0 }
        };

        var cohortHeatmap = new List<RetentionCohortDto>();
        var cohortGroups = patientInvoicesGrouped
            .GroupBy(p => p.FirstVisit.ToString("yyyy-MM"))
            .OrderBy(g => g.Key)
            .Take(6)
            .ToList();

        foreach (var cg in cohortGroups)
        {
            var cohortMonth = cg.Key;
            var cohortPatients = cg.ToList();
            var size = cohortPatients.Count;

            var rates = new List<double> { 100.0 };

            var parts = cohortMonth.Split('-');
            var year = parts.Length > 0 && int.TryParse(parts[0], out var y) ? y : referenceDate.Year;
            var month = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : referenceDate.Month;
            var cohortStartDateTime = new DateTime(year, month, 1);

            for (int offset = 1; offset <= 5; offset++)
            {
                var targetMonthStart = cohortStartDateTime.AddMonths(offset);
                var targetMonthEnd = targetMonthStart.AddMonths(1).AddTicks(-1);

                var activeCount = cohortPatients
                    .Count(p => p.Visits.Any(v => v >= targetMonthStart && v <= targetMonthEnd));

                var rate = size > 0 ? Math.Round((double)activeCount / size * 100, 1) : 0;
                rates.Add(rate);
            }

            cohortHeatmap.Add(new RetentionCohortDto
            {
                CohortMonth = cohortMonth,
                Size = size,
                RetentionRates = rates
            });
        }

        var churnAlerts = new List<PatientChurnAlertDto>();
        foreach (var group in activeInvoices.GroupBy(i => i.PatientId))
        {
            var invoices = group.OrderByDescending(i => i.ServiceDate).ToList();
            var lastInvoice = invoices.First();
            var daysSince = (referenceDate - lastInvoice.ServiceDate).Days;

            if (daysSince > 45 && daysSince <= 180)
            {
                var name = lastInvoice.PatientName ?? "Anonymous Patient";
                churnAlerts.Add(new PatientChurnAlertDto
                {
                    PatientName = name,
                    LastModality = lastInvoice.Modality ?? "Unknown",
                    LastScanDate = lastInvoice.ServiceDate,
                    DaysSinceLastScan = daysSince,
                    RiskLevel = daysSince > 90 ? "CRITICAL" : "ELEVATED"
                });
            }
        }
        churnAlerts = churnAlerts.OrderByDescending(c => c.DaysSinceLastScan).Take(3).ToList();

        return new PatientLtvDto
        {
            AverageOrderValue = Math.Round(aov, 2),
            PurchaseFrequency = Math.Round(pf, 2),
            PatientValue = Math.Round(pv, 2),
            EstimatedLifetimeValue = Math.Round(estimatedLtv, 2),
            Segments = ltvSegments,
            RetentionHeatmap = cohortHeatmap,
            ChurnAlerts = churnAlerts
        };
    }
}
