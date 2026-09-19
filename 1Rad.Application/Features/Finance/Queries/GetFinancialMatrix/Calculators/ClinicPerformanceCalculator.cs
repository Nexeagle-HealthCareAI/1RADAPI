namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class ClinicPerformanceCalculator : IClinicPerformanceCalculator
{
    public ClinicPerformanceDto Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices, IReadOnlyList<ExpenseMatrixRow> expenseData)
    {
        var totalGross = activeInvoices.Sum(i => i.GrossAmount);
        var totalNet = activeInvoices.Sum(i => i.TotalAmount);
        var totalPaid = activeInvoices.Sum(i => i.PaidAmount);
        var totalDiscount = activeInvoices.Sum(i => i.DiscountAmount);
        var totalExpenses = expenseData.Sum(e => e.Amount + e.TaxAmount);

        return new ClinicPerformanceDto
        {
            GrossRevenue = totalGross,
            CashCollected = totalPaid,
            ConcessionLeakage = totalDiscount,
            LeakagePercentage = totalGross > 0 ? (double)Math.Round((totalDiscount / totalGross) * 100, 1) : 0,
            // Floored per invoice — an unfloored sum let one overpaid invoice
            // (PaidAmount > TotalAmount; the excess is parked as a patient
            // credit, not a negative balance) subtract from every OTHER
            // invoice's genuine pending balance, understating total AR owed.
            // Matches the convention already used for this same figure on the
            // Revenue tab (PENDING AMOUNT).
            OutstandingAR = activeInvoices.Sum(i => Math.Max(0, i.TotalAmount - i.PaidAmount)),
            ExpenseRatio = totalPaid > 0 ? (double)Math.Round((totalExpenses / totalPaid) * 100, 1) : 0,
            AverageRevenuePerScan = activeInvoices.Any() ? Math.Round(totalNet / activeInvoices.Count(), 2) : 0,
            TotalScansCount = activeInvoices.Count()
        };
    }
}
