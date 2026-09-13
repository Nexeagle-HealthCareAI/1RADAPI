using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance.GetFinancialMatrix;

// No DbContext, no InMemory provider, no BaseHandlerTest — this is the whole
// point of Phase 1: the calculator is a pure function of plain rows, so a
// test is just "build some rows, call it, assert on the result."
public class AgingAnalysisCalculatorTests
{
    private readonly AgingAnalysisCalculator _calculator = new();
    private static readonly DateTime Today = new(2026, 9, 13);

    private static InvoiceMatrixRow Outstanding(decimal total, decimal paid, int ageInDays) => new()
    {
        Id = Guid.NewGuid(),
        TotalAmount = total,
        PaidAmount = paid,
        ServiceDate = Today.AddDays(-ageInDays),
    };

    [Fact]
    public void Calculate_BucketsByAgeSinceServiceDate()
    {
        var invoices = new List<InvoiceMatrixRow>
        {
            Outstanding(total: 1000, paid: 0, ageInDays: 10),   // 0-30
            Outstanding(total: 2000, paid: 0, ageInDays: 45),   // 31-60
            Outstanding(total: 3000, paid: 0, ageInDays: 75),   // 61-90
            Outstanding(total: 4000, paid: 0, ageInDays: 120),  // 91+
        };

        var result = _calculator.Calculate(invoices, Today);

        result.Bucket0To30.Should().Be(1000);
        result.Bucket31To60.Should().Be(2000);
        result.Bucket61To90.Should().Be(3000);
        result.Bucket91Plus.Should().Be(4000);
        result.TotalOutstanding.Should().Be(10000);
    }

    [Fact]
    public void Calculate_ExcludesFullyPaidInvoices()
    {
        var invoices = new List<InvoiceMatrixRow>
        {
            Outstanding(total: 1000, paid: 1000, ageInDays: 120), // fully paid — must not count as outstanding
        };

        var result = _calculator.Calculate(invoices, Today);

        result.TotalOutstanding.Should().Be(0);
    }

    [Fact]
    public void Calculate_BoundaryDayCountsInTheLowerBucket()
    {
        // Exactly 30 days old belongs in 0-30, not 31-60 (i > 30, not i >= 30).
        var invoices = new List<InvoiceMatrixRow>
        {
            Outstanding(total: 500, paid: 0, ageInDays: 30),
        };

        var result = _calculator.Calculate(invoices, Today);

        result.Bucket0To30.Should().Be(500);
        result.Bucket31To60.Should().Be(0);
    }

    [Fact]
    public void Calculate_NoInvoices_ReturnsAllZeroBuckets()
    {
        var result = _calculator.Calculate(new List<InvoiceMatrixRow>(), Today);

        result.TotalOutstanding.Should().Be(0);
    }
}
