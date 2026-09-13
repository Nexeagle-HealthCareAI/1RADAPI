namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class PaymentChannelCalculator : IPaymentChannelCalculator
{
    public PaymentChannelBreakdownDto Calculate(IReadOnlyList<PaymentMatrixRow> paymentData)
    {
        decimal SumFor(string method) => paymentData
            .Where(p => p.PaymentMethod != null && p.PaymentMethod.Equals(method, StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.Amount);

        return new PaymentChannelBreakdownDto
        {
            CashAmount = SumFor("CASH"),
            UpiAmount = SumFor("UPI"),
            CardAmount = SumFor("CARD"),
            AdvanceAmount = SumFor("ADVANCE")
        };
    }
}
