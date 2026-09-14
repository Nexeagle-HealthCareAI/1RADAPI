using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance.GetFinancialMatrix;

public class DiscountAllocationCalculatorTests
{
    private readonly DiscountAllocationCalculator _calculator = new();

    [Fact]
    public void Calculate_SumsEachRecordedDeductionVector()
    {
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { CentreDiscount = 100, ReferrerDiscount = 50, InstitutionalDeduction = 25, DiscountAmount = 175 },
            new() { CentreDiscount = 200, ReferrerDiscount = 0, InstitutionalDeduction = 0, DiscountAmount = 200 },
        };

        var result = _calculator.Calculate(invoices);

        result.Centre.Should().Be(300);
        result.Referrer.Should().Be(50);
        result.Institutional.Should().Be(25);
        result.Other.Should().Be(0);
    }

    [Fact]
    public void Calculate_ResidualDiscountNotCoveredByVectors_GoesToOther()
    {
        // DiscountAmount (375) exceeds the sum of the three named vectors (300) —
        // e.g. a manual ad-hoc adjustment recorded only in the aggregate field.
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { CentreDiscount = 200, ReferrerDiscount = 100, InstitutionalDeduction = 0, DiscountAmount = 375 },
        };

        var result = _calculator.Calculate(invoices);

        result.Other.Should().Be(75);
    }

    [Fact]
    public void Calculate_NeverReturnsNegativeOther()
    {
        // Defensive: named vectors summing to MORE than DiscountAmount (shouldn't
        // happen, but the original code clamps at zero rather than going negative).
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { CentreDiscount = 500, ReferrerDiscount = 0, InstitutionalDeduction = 0, DiscountAmount = 100 },
        };

        var result = _calculator.Calculate(invoices);

        result.Other.Should().Be(0);
    }
}
