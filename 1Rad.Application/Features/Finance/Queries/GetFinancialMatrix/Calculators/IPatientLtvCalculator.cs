namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>
/// Patient lifetime value, high/mid/low value segmentation, monthly cohort
/// retention heatmap, and churn-risk alerts (45-180 days since last visit).
/// Takes <paramref name="referenceDate"/> explicitly rather than reading
/// DateTime.UtcNow internally — a calculator with a hidden clock read isn't
/// a pure function of its inputs and can't be given a fixed "today" in a
/// test, which is exactly the kind of implicit dependency this refactor is
/// removing.
/// </summary>
public interface IPatientLtvCalculator
{
    PatientLtvDto Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices, DateTime referenceDate);
}
