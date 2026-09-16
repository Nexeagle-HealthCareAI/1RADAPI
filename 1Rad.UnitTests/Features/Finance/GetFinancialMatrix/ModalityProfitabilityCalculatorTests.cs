using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance.GetFinancialMatrix;

// A hand-written fake standing in for IModalityExpenseAllocator — this is the
// payoff of the Dependency Inversion boundary drawn in ModalityProfitabilityCalculator:
// the profitability math can be tested with a fixed, known cost allocation
// instead of also exercising the keyword-matching logic every time.
public class FixedModalityExpenseAllocator : IModalityExpenseAllocator
{
    private readonly ModalityExpenseAllocation _fixedResult;
    public FixedModalityExpenseAllocator(ModalityExpenseAllocation fixedResult) => _fixedResult = fixedResult;
    public ModalityExpenseAllocation Allocate(IReadOnlyList<ExpenseMatrixRow> expenseData) => _fixedResult;
}

public class ModalityProfitabilityCalculatorTests
{
    private static ServiceLineRow Line(string modality, string service, decimal gross, decimal paid, decimal referralCut = 0, decimal? total = null) => new()
    {
        Modality = modality,
        ServiceName = service,
        Gross = gross,
        Total = total ?? gross,
        Paid = paid,
        ReferralCut = referralCut,
    };

    [Fact]
    public void Calculate_NetRevenue_SubtractsReferralCutFromGross()
    {
        var allocator = new FixedModalityExpenseAllocator(new ModalityExpenseAllocation());
        var calculator = new ModalityProfitabilityCalculator(allocator);

        var lines = new List<ServiceLineRow> { Line("MRI", "Brain MRI", gross: 10000, paid: 10000, referralCut: 1500) };

        var result = calculator.Calculate(lines, new List<ExpenseMatrixRow>());

        var mri = result.Should().ContainSingle().Subject;
        mri.GrossRevenue.Should().Be(10000);
        mri.ReferralCut.Should().Be(1500);
        mri.NetRevenue.Should().Be(8500);
    }

    [Fact]
    public void Calculate_NetRevenue_UsesPostDiscountTotal_NotPreDiscountGross()
    {
        // A ₹10,000 list-price scan billed at ₹8,000 after a discount, with a
        // ₹500 referral cut. Net must be the post-discount 8,000 - 500 = 7,500 —
        // not the pre-discount 10,000 - 500 = 9,500 the old gross-based formula
        // gave, which silently ignored every discount on the invoice.
        var allocator = new FixedModalityExpenseAllocator(new ModalityExpenseAllocation());
        var calculator = new ModalityProfitabilityCalculator(allocator);

        var lines = new List<ServiceLineRow> { Line("MRI", "Brain MRI", gross: 10000, paid: 8000, referralCut: 500, total: 8000) };

        var result = calculator.Calculate(lines, new List<ExpenseMatrixRow>());

        var mri = result.Should().ContainSingle().Subject;
        mri.GrossRevenue.Should().Be(10000);
        mri.NetRevenue.Should().Be(7500);
        mri.CollectionEfficiency.Should().Be(100); // fully paid relative to what was actually billed
    }

    [Fact]
    public void Calculate_OperatingCost_CombinesDirectPlusProportionalOverheadShare()
    {
        var allocation = new ModalityExpenseAllocation
        {
            DirectByModality = new Dictionary<string, decimal> { ["MRI"] = 1000 },
            GeneralRadiologyOverhead = 2000,
        };
        var calculator = new ModalityProfitabilityCalculator(new FixedModalityExpenseAllocator(allocation));

        // 3 MRI scans + 1 CT scan = 4 total scans. MRI's overhead share = 3/4 * 2000 = 1500.
        var lines = new List<ServiceLineRow>
        {
            Line("MRI", "Brain MRI", 5000, 5000),
            Line("MRI", "Spine MRI", 5000, 5000),
            Line("MRI", "Knee MRI", 5000, 5000),
            Line("CT", "Chest CT", 3000, 3000),
        };

        var result = calculator.Calculate(lines, new List<ExpenseMatrixRow>());

        var mri = result.Single(m => m.Modality == "MRI");
        mri.OperatingCost.Should().Be(1000 + 1500); // direct + proportional share
    }

    [Fact]
    public void Calculate_ZeroOperatingCost_GivesZeroRoiNotDivideByZeroError()
    {
        var calculator = new ModalityProfitabilityCalculator(new FixedModalityExpenseAllocator(new ModalityExpenseAllocation()));
        var lines = new List<ServiceLineRow> { Line("USG", "Abdomen USG", 2000, 2000) };

        var result = calculator.Calculate(lines, new List<ExpenseMatrixRow>());

        result.Single().EquipmentRoiRatio.Should().Be(0);
    }

    [Fact]
    public void Calculate_GroupsServicesWithinEachModality()
    {
        var calculator = new ModalityProfitabilityCalculator(new FixedModalityExpenseAllocator(new ModalityExpenseAllocation()));
        var lines = new List<ServiceLineRow>
        {
            Line("MRI", "Brain MRI", 5000, 5000),
            Line("MRI", "Spine MRI", 3000, 3000),
        };

        var result = calculator.Calculate(lines, new List<ExpenseMatrixRow>());

        var mri = result.Single();
        mri.Services.Should().HaveCount(2);
        mri.Services.Select(s => s.ServiceName).Should().Contain(new[] { "Brain MRI", "Spine MRI" });
    }
}
