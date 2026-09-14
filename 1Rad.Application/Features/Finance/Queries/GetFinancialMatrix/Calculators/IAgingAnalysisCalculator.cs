namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>Buckets outstanding (unpaid) balance by age since ServiceDate: 0-30/31-60/61-90/91+ days.</summary>
public interface IAgingAnalysisCalculator
{
    AgingAnalysisDto Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices, DateTime referenceDate);
}
