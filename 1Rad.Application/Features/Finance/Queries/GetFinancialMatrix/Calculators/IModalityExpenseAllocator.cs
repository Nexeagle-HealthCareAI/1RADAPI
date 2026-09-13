namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class ModalityExpenseAllocation
{
    public Dictionary<string, decimal> DirectByModality { get; set; } = new();
    public decimal GeneralRadiologyOverhead { get; set; }
}

/// <summary>
/// Attributes each operational expense to a modality (by Description/CostCenter
/// keyword) or, failing that, to the shared "general Radiology overhead" pool.
/// The keyword table is data, not branching logic — adding a modality (e.g. PET,
/// Mammography) is a one-line addition to <see cref="ModalityExpenseAllocator.KeywordMap"/>,
/// not a new "else if" (Open/Closed: this class is closed for modification,
/// open for extension via the table).
/// </summary>
public interface IModalityExpenseAllocator
{
    ModalityExpenseAllocation Allocate(IReadOnlyList<ExpenseMatrixRow> expenseData);
}
