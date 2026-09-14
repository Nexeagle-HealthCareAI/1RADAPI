using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance.GetFinancialMatrix;

public class PhysicianRoiCalculatorTests
{
    private readonly PhysicianRoiCalculator _calculator = new();

    [Fact]
    public void Calculate_PairsRevenueWithCommissionForAnExactNameMatch()
    {
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { HasReferrer = true, ReferredBy = "Dr. Smith", TotalAmount = 1000 },
        };
        var commissions = new List<CommissionMatrixRow> { new() { ReferrerName = "Dr. Smith", CommissionAmount = 150 } };

        var result = _calculator.Calculate(invoices, commissions);

        result.Single().Should().BeEquivalentTo(new { DoctorName = "Dr. Smith", BilledRevenue = 1000m, CommissionPaid = 150m });
    }

    [Theory]
    [InlineData("dr. smith")]        // case difference
    [InlineData("Dr. Smith ")]       // trailing whitespace
    [InlineData(" Dr. Smith")]       // leading whitespace
    [InlineData("DR. SMITH")]        // all caps
    public void Calculate_MatchesDespiteCaseOrWhitespaceDriftBetweenTheTwoTables(string commissionSideName)
    {
        // Invoice.ReferredBy and ReferralCommission.ReferrerName are two
        // independently-typed free-text fields — this is the exact class of
        // drift that used to silently split one doctor into two rows.
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { HasReferrer = true, ReferredBy = "Dr. Smith", TotalAmount = 1000 },
        };
        var commissions = new List<CommissionMatrixRow> { new() { ReferrerName = commissionSideName, CommissionAmount = 150 } };

        var result = _calculator.Calculate(invoices, commissions);

        result.Should().ContainSingle();
        result[0].CommissionPaid.Should().Be(150);
    }

    [Fact]
    public void Calculate_DisplayNameComesFromTheInvoiceSide_NotNormalized()
    {
        // The dictionary lookup is normalized; what's actually shown to the
        // user must stay the original, human-entered casing/spacing.
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { HasReferrer = true, ReferredBy = "Dr. Smith", TotalAmount = 1000 },
        };

        var result = _calculator.Calculate(invoices, new List<CommissionMatrixRow>());

        result.Single().DoctorName.Should().Be("Dr. Smith");
    }

    [Fact]
    public void Calculate_DoctorWithNoRecordedCommission_ShowsZero()
    {
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { HasReferrer = true, ReferredBy = "Dr. NoPayout", TotalAmount = 500 },
        };

        var result = _calculator.Calculate(invoices, new List<CommissionMatrixRow>());

        result.Single().CommissionPaid.Should().Be(0);
    }

    [Fact]
    public void Calculate_DirectWalkInInvoices_AreExcluded()
    {
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { HasReferrer = false, TotalAmount = 500 },
        };

        var result = _calculator.Calculate(invoices, new List<CommissionMatrixRow>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_SortsDescendingByBilledRevenue()
    {
        var invoices = new List<InvoiceMatrixRow>
        {
            new() { HasReferrer = true, ReferredBy = "Dr. Small", TotalAmount = 100 },
            new() { HasReferrer = true, ReferredBy = "Dr. Big", TotalAmount = 9000 },
        };

        var result = _calculator.Calculate(invoices, new List<CommissionMatrixRow>());

        result[0].DoctorName.Should().Be("Dr. Big");
        result[1].DoctorName.Should().Be("Dr. Small");
    }
}
