namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>Billed revenue vs. commission paid per referring physician.</summary>
public interface IPhysicianRoiCalculator
{
    List<PhysicianRoiDto> Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices, IReadOnlyList<CommissionMatrixRow> commissionData);
}
