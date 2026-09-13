namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>Net revenue per modality, and each modality's share of lifetime invoiced revenue.</summary>
public interface IModalityRevenueCalculator
{
    List<ModalityRevenueDto> Calculate(IReadOnlyList<ServiceLineRow> serviceLines, decimal totalLifetimeInvoiced);
}
