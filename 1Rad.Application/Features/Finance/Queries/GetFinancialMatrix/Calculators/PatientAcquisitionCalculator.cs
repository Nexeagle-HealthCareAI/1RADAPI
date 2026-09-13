namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class PatientAcquisitionCalculator : IPatientAcquisitionCalculator
{
    public List<PatientAcquisitionCohortDto> Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices)
    {
        var firstServiceMonth = activeInvoices
            .GroupBy(i => i.PatientId)
            .ToDictionary(
                g => g.Key,
                g => { var f = g.Min(x => x.ServiceDate); return (f.Year, f.Month); });

        var monthlyCohorts = activeInvoices
            .GroupBy(i => new { i.ServiceDate.Year, i.ServiceDate.Month })
            .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
            .Take(6)
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .ToList();

        var result = new List<PatientAcquisitionCohortDto>();
        foreach (var monthGroup in monthlyCohorts)
        {
            var label = new DateTime(monthGroup.Key.Year, monthGroup.Key.Month, 1).ToString("MMM");
            int newCount = 0;
            int retCount = 0;
            foreach (var patientId in monthGroup.Select(i => i.PatientId).Distinct())
            {
                var fm = firstServiceMonth[patientId];
                if (fm.Year == monthGroup.Key.Year && fm.Month == monthGroup.Key.Month) newCount++;
                else retCount++;
            }
            result.Add(new PatientAcquisitionCohortDto
            {
                MonthLabel = label,
                NewPatientsCount = newCount,
                ReturningPatientsCount = retCount
            });
        }

        return result;
    }
}
