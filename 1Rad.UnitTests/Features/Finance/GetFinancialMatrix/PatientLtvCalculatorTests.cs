using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance.GetFinancialMatrix;

public class PatientLtvCalculatorTests
{
    private readonly PatientLtvCalculator _calculator = new();

    // Deterministic "today" passed explicitly — no DateTime.UtcNow inside the
    // calculator, so this test's outcome can never depend on when it happens
    // to run (the original inline code read the clock directly, mid-method).
    private static readonly DateTime FixedToday = new(2026, 9, 13);

    private static InvoiceMatrixRow Visit(Guid patientId, string? name, DateTime serviceDate, decimal total) => new()
    {
        Id = Guid.NewGuid(),
        PatientId = patientId,
        PatientName = name,
        TotalAmount = total,
        ServiceDate = serviceDate,
    };

    [Fact]
    public void Calculate_ChurnAlert_FiresOnlyWithinThe45To180DayWindow()
    {
        var justUnder = Guid.NewGuid();
        var inWindow = Guid.NewGuid();
        var tooOld = Guid.NewGuid();

        var invoices = new List<InvoiceMatrixRow>
        {
            Visit(justUnder, "Patient A", FixedToday.AddDays(-40), 1000),  // 40 days — too recent, no alert
            Visit(inWindow, "Patient B", FixedToday.AddDays(-60), 1000),   // 60 days — ELEVATED
            Visit(tooOld, "Patient C", FixedToday.AddDays(-200), 1000),    // 200 days — too old, no alert
        };

        var result = _calculator.Calculate(invoices, FixedToday);

        result.ChurnAlerts.Should().ContainSingle(a => a.PatientName == "Patient B");
        result.ChurnAlerts.Should().NotContain(a => a.PatientName == "Patient A" || a.PatientName == "Patient C");
    }

    [Fact]
    public void Calculate_ChurnRiskLevel_CriticalPastNinetyDays()
    {
        var patientId = Guid.NewGuid();
        var invoices = new List<InvoiceMatrixRow> { Visit(patientId, "Patient X", FixedToday.AddDays(-100), 1000) };

        var result = _calculator.Calculate(invoices, FixedToday);

        result.ChurnAlerts.Single().RiskLevel.Should().Be("CRITICAL");
    }

    [Fact]
    public void Calculate_HighValueSegment_UsesFifteenThousandThreshold()
    {
        var patientId = Guid.NewGuid();
        var invoices = new List<InvoiceMatrixRow> { Visit(patientId, "Big Spender", FixedToday, 20000) };

        var result = _calculator.Calculate(invoices, FixedToday);

        var highTier = result.Segments.Single(s => s.Tier == "High Value");
        highTier.PatientCount.Should().Be(1);
        highTier.TotalRevenue.Should().Be(20000);
    }

    [Fact]
    public void Calculate_MultipleVisitsSamePatient_CountedOnceForAcquisitionButSummedForRevenue()
    {
        var patientId = Guid.NewGuid();
        var invoices = new List<InvoiceMatrixRow>
        {
            Visit(patientId, "Repeat Patient", FixedToday.AddMonths(-2), 3000),
            Visit(patientId, "Repeat Patient", FixedToday, 4000),
        };

        var result = _calculator.Calculate(invoices, FixedToday);

        // 2 invoices / 1 unique patient => purchase frequency 2.0
        result.PurchaseFrequency.Should().Be(2.0);
        result.AverageOrderValue.Should().Be(3500); // (3000+4000)/2 invoices
    }

    [Fact]
    public void Calculate_NoPatients_ReturnsZeroedResultWithoutThrowing()
    {
        var result = _calculator.Calculate(new List<InvoiceMatrixRow>(), FixedToday);

        result.AverageOrderValue.Should().Be(0);
        result.ChurnAlerts.Should().BeEmpty();
        result.Segments.Should().HaveCount(3); // High/Mid/Low always present, just empty
        result.Segments.Should().OnlyContain(s => s.PatientCount == 0);
    }
}
