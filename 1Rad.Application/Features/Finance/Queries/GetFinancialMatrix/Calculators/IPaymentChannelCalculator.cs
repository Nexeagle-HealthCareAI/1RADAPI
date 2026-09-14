namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

/// <summary>
/// Cash/UPI/Card/Advance breakdown of payments received. ADVANCE-tagged
/// payments (an invoice settled from a patient's existing credit balance)
/// are deliberately excluded from TotalCollected — no fresh money moved —
/// and surfaced separately for transparency.
/// </summary>
public interface IPaymentChannelCalculator
{
    PaymentChannelBreakdownDto Calculate(IReadOnlyList<PaymentMatrixRow> paymentData);
}
