using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance.GetFinancialMatrix;

public class TemporalAggregationCalculatorTests
{
    private readonly TemporalAggregationCalculator _calculator = new();

    [Fact]
    public void Calculate_Weekly_DoesNotMergeTheSameWeekNumberAcrossDifferentYears()
    {
        // Week 2 of 2025 and week 2 of 2026 must stay separate buckets — a
        // plain GroupBy(ISOWeek.GetWeekOfYear) merges them, silently combining
        // a year-old week's revenue into the current year's "Week 2" on any
        // date range spanning more than a year (e.g. "ALL TIME").
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { ServiceDate = new DateTime(2025, 1, 8), TotalAmount = 1000, PaidAmount = 1000 }, // ISO week 2, 2025
            new() { ServiceDate = new DateTime(2026, 1, 8), TotalAmount = 2000, PaidAmount = 2000 }, // ISO week 2, 2026
        };

        var result = _calculator.Calculate(invoices, new List<ExpenseMatrixRow>());

        result.Weekly.Should().HaveCount(2);
        result.Weekly.Should().Contain(w => w.Label.Contains("2025") && w.Invoiced == 1000);
        result.Weekly.Should().Contain(w => w.Label.Contains("2026") && w.Invoiced == 2000);
    }

    [Fact]
    public void Calculate_Weekly_MatchesExpensesToTheCorrectYearedWeek()
    {
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { ServiceDate = new DateTime(2026, 1, 8), TotalAmount = 1000, PaidAmount = 1000 },
        };
        var expenses = new List<ExpenseMatrixRow>
        {
            new() { TransactionDate = new DateTime(2025, 1, 8), Amount = 500, TaxAmount = 0 }, // same week number, different year
            new() { TransactionDate = new DateTime(2026, 1, 8), Amount = 200, TaxAmount = 0 },
        };

        var result = _calculator.Calculate(invoices, expenses);

        var week2026 = result.Weekly.Single(w => w.Label.Contains("2026"));
        week2026.Expenses.Should().Be(200); // not 700 — the 2025 expense must not leak in
    }

    [Fact]
    public void Calculate_Daily_BucketsByExactCalendarDate()
    {
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { ServiceDate = new DateTime(2026, 9, 1), TotalAmount = 500, PaidAmount = 300 },
            new() { ServiceDate = new DateTime(2026, 9, 1, 18, 0, 0), TotalAmount = 500, PaidAmount = 500 },
        };

        var result = _calculator.Calculate(invoices, new List<ExpenseMatrixRow>());

        result.Daily.Should().ContainSingle();
        result.Daily[0].Invoiced.Should().Be(1000);
        result.Daily[0].Collected.Should().Be(800);
    }
}
