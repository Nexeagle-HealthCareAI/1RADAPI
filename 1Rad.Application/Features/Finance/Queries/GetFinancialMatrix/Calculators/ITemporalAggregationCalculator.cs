namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class TemporalAggregationResult
{
    public List<MatrixItemDto> Daily { get; set; } = new();
    public List<MatrixItemDto> Weekly { get; set; } = new();
    public List<MatrixItemDto> Monthly { get; set; } = new();
    public List<MatrixItemDto> Yearly { get; set; } = new();
}

/// <summary>
/// Buckets active invoices + expenses into daily/weekly/monthly/yearly rollups
/// (Invoiced/Collected/Expenses/Pending/RealizationRate per period), bucketed
/// by ServiceDate (invoices) and TransactionDate (expenses).
/// </summary>
public interface ITemporalAggregationCalculator
{
    TemporalAggregationResult Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices, IReadOnlyList<ExpenseMatrixRow> expenseData);
}
