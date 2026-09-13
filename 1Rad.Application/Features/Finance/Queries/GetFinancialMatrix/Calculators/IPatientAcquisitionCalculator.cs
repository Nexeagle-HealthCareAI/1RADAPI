namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>
/// New-vs-returning patient counts per month (last 6 months present in the
/// data). A patient is "new" in the month of their first-ever service in
/// this data set, "returning" in any later month.
/// </summary>
public interface IPatientAcquisitionCalculator
{
    List<PatientAcquisitionCohortDto> Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices);
}
