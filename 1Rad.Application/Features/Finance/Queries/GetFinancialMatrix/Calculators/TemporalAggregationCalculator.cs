using System.Globalization;

namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class TemporalAggregationCalculator : ITemporalAggregationCalculator
{
    public TemporalAggregationResult Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices, IReadOnlyList<ExpenseMatrixRow> expenseData)
    {
        var daily = activeInvoices
            .GroupBy(i => i.ServiceDate.Date)
            .Select(g => new { Date = g.Key, Invoiced = g.Sum(i => i.TotalAmount), Collected = g.Sum(i => i.PaidAmount) })
            .Concat(expenseData.GroupBy(e => e.TransactionDate.Date).Select(g => new { Date = g.Key, Invoiced = 0m, Collected = 0m }))
            .GroupBy(x => x.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new MatrixItemDto
            {
                Label = g.Key.ToString("dd-MMM-yyyy"),
                Invoiced = g.Sum(x => x.Invoiced),
                Collected = g.Sum(x => x.Collected),
                Expenses = expenseData.Where(e => e.TransactionDate.Date == g.Key).Sum(e => e.Amount + e.TaxAmount),
                Pending = g.Sum(x => x.Invoiced - x.Collected),
                RealizationRate = g.Sum(x => x.Invoiced) > 0
                    ? Math.Min(100, (int)(g.Sum(x => x.Collected) / g.Sum(x => x.Invoiced) * 100))
                    : 0
            }).Take(30).ToList();

        // Keyed by (ISO week-year, week number) — not week number alone. ISO
        // week-year can differ from calendar year near Dec/Jan boundaries,
        // which is exactly why ISOWeek.GetYear (not .ServiceDate.Year) pairs
        // correctly with GetWeekOfYear. Grouping by week number alone merged
        // "Week 34" of every year in the data into one bucket on any date
        // range spanning more than a year (e.g. the "ALL TIME" filter).
        var weekly = activeInvoices
            .GroupBy(i => (Year: ISOWeek.GetYear(i.ServiceDate), Week: ISOWeek.GetWeekOfYear(i.ServiceDate)))
            .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Week)
            .Select(g => new MatrixItemDto
            {
                Label = $"Week {g.Key.Week}, {g.Key.Year}",
                Invoiced = g.Sum(i => i.TotalAmount),
                Collected = g.Sum(i => i.PaidAmount),
                Expenses = expenseData
                    .Where(e => ISOWeek.GetYear(e.TransactionDate) == g.Key.Year && ISOWeek.GetWeekOfYear(e.TransactionDate) == g.Key.Week)
                    .Sum(e => e.Amount + e.TaxAmount),
                Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                RealizationRate = g.Sum(i => i.TotalAmount) > 0
                    ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
                    : 0
            }).Take(8).ToList();

        var monthly = activeInvoices
            .GroupBy(i => new { i.ServiceDate.Year, i.ServiceDate.Month })
            .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
            .Select(g => new MatrixItemDto
            {
                Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM yyyy"),
                Invoiced = g.Sum(i => i.TotalAmount),
                Collected = g.Sum(i => i.PaidAmount),
                Expenses = expenseData.Where(e => e.TransactionDate.Year == g.Key.Year && e.TransactionDate.Month == g.Key.Month).Sum(e => e.Amount + e.TaxAmount),
                Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                RealizationRate = g.Sum(i => i.TotalAmount) > 0
                    ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
                    : 0
            }).Take(12).ToList();

        var yearly = activeInvoices
            .GroupBy(i => i.ServiceDate.Year)
            .OrderByDescending(g => g.Key)
            .Select(g => new MatrixItemDto
            {
                Label = g.Key.ToString(),
                Invoiced = g.Sum(i => i.TotalAmount),
                Collected = g.Sum(i => i.PaidAmount),
                Expenses = expenseData.Where(e => e.TransactionDate.Year == g.Key).Sum(e => e.Amount + e.TaxAmount),
                Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                RealizationRate = g.Sum(i => i.TotalAmount) > 0
                    ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
                    : 0
            }).ToList();

        return new TemporalAggregationResult { Daily = daily, Weekly = weekly, Monthly = monthly, Yearly = yearly };
    }
}
