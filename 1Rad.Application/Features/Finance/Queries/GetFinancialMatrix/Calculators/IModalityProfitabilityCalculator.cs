namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>
/// The clinical modality profitability matrix — gross/net/operating profit,
/// margins, ROI and break-even scan count per modality, with a per-service
/// breakdown under each. Depends on <see cref="IModalityExpenseAllocator"/>
/// (constructor-injected) to attribute operating costs, rather than doing its
/// own expense-string-matching — a Dependency Inversion boundary: swap the
/// allocator's implementation (e.g. a per-hospital configurable cost-center
/// mapping) without touching the profitability math at all.
/// </summary>
public interface IModalityProfitabilityCalculator
{
    List<ModalityProfitabilityDto> Calculate(IReadOnlyList<ServiceLineRow> serviceLines, IReadOnlyList<ExpenseMatrixRow> expenseData);
}
