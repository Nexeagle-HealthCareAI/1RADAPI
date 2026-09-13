namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>Headline clinic-wide performance stats: gross revenue, cash collected, leakage, AR, expense ratio.</summary>
public interface IClinicPerformanceCalculator
{
    ClinicPerformanceDto Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices, IReadOnlyList<ExpenseMatrixRow> expenseData);
}
