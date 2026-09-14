using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance.GetFinancialMatrix;

public class ModalityExpenseAllocatorTests
{
    private readonly ModalityExpenseAllocator _allocator = new();

    [Fact]
    public void Allocate_MatchesByDescriptionKeyword()
    {
        var expenses = new List<ExpenseMatrixRow>
        {
            new() { Amount = 1000, TaxAmount = 0, Description = "MRI coil annual service" },
        };

        var result = _allocator.Allocate(expenses);

        result.DirectByModality.Should().ContainKey("MRI").WhoseValue.Should().Be(1000);
    }

    [Fact]
    public void Allocate_MatchesByExactCostCenter_WhenDescriptionDoesNotMention()
    {
        var expenses = new List<ExpenseMatrixRow>
        {
            new() { Amount = 500, TaxAmount = 50, Description = "Quarterly maintenance", CostCenter = "CT" },
        };

        var result = _allocator.Allocate(expenses);

        result.DirectByModality["CT"].Should().Be(550); // includes tax
    }

    [Fact]
    public void Allocate_FirstRuleWins_WhenMultipleKeywordsPresent()
    {
        // "MRI-CT combo room" mentions both — MRI is listed first in the rule
        // table, matching the original if/else-if chain's priority (MRI checked
        // before CT), so it must be attributed to MRI, not CT or split.
        var expenses = new List<ExpenseMatrixRow>
        {
            new() { Amount = 1000, TaxAmount = 0, Description = "MRI-CT combo room electricity" },
        };

        var result = _allocator.Allocate(expenses);

        result.DirectByModality.Should().ContainKey("MRI");
        result.DirectByModality.Should().NotContainKey("CT");
    }

    [Fact]
    public void Allocate_UnmatchedRadiologyCostCenter_GoesToGeneralOverhead()
    {
        var expenses = new List<ExpenseMatrixRow>
        {
            new() { Amount = 300, TaxAmount = 0, CostCenter = "Radiology" },
        };

        var result = _allocator.Allocate(expenses);

        result.GeneralRadiologyOverhead.Should().Be(300);
        result.DirectByModality.Should().BeEmpty();
    }

    [Fact]
    public void Allocate_TrulyUnrelatedExpense_IsExcludedEntirely()
    {
        // Neither a modality keyword nor Radiology/Maintenance — e.g. office
        // rent. Not attributed anywhere (matches the original's silent skip).
        var expenses = new List<ExpenseMatrixRow>
        {
            new() { Amount = 5000, TaxAmount = 0, Description = "Office rent", CostCenter = "Admin", Category = "Rent" },
        };

        var result = _allocator.Allocate(expenses);

        result.DirectByModality.Should().BeEmpty();
        result.GeneralRadiologyOverhead.Should().Be(0);
    }

    [Theory]
    [InlineData("Contract renewal fees")]      // contains "ct" inside "Contract"
    [InlineData("Electricity bill - March")]   // contains "ct" inside "Electricity"
    [InlineData("Structural inspection")]      // contains "ct" inside "Structural"
    public void Allocate_DoesNotFalsePositiveOnCtSubstring(string description)
    {
        // "CT" must match as a standalone word, not as a substring of an
        // unrelated word — these three all contain the letters "ct" but none
        // of them mention the CT modality.
        var expenses = new List<ExpenseMatrixRow> { new() { Amount = 1000, TaxAmount = 0, Description = description } };

        var result = _allocator.Allocate(expenses);

        result.DirectByModality.Should().NotContainKey("CT");
    }

    [Fact]
    public void Allocate_StillMatchesCtAsAStandaloneWord()
    {
        var expenses = new List<ExpenseMatrixRow> { new() { Amount = 1000, TaxAmount = 0, Description = "CT tube replacement" } };

        var result = _allocator.Allocate(expenses);

        result.DirectByModality["CT"].Should().Be(1000);
    }
}
