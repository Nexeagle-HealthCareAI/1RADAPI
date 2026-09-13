namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class PhysicianRoiCalculator : IPhysicianRoiCalculator
{
    // Referrer identity here is a free-text name on both sides —
    // Invoice.ReferredBy and ReferralCommission.ReferrerName are two
    // independently-denormalized string fields on different tables/write
    // paths, not a shared ReferrerId. Trim + case-fold before matching so
    // "Dr. Smith" (invoice) and "dr.  smith" (commission) still join instead
    // of silently splitting into two rows — one with revenue and no
    // commission, one with commission and no revenue. This does not fix a
    // genuine spelling variant ("Dr Smith" vs "Dr. Smyth"); that needs both
    // sides to resolve through the same ReferrerId, a bigger data-model
    // change than this calculator can safely make on its own.
    private static string NormalizeKey(string name) => name.Trim().ToUpperInvariant();

    public List<PhysicianRoiDto> Calculate(IReadOnlyList<InvoiceMatrixRow> activeInvoices, IReadOnlyList<CommissionMatrixRow> commissionData)
    {
        var doctorRevenue = activeInvoices
            .Where(i => i.HasReferrer && !string.IsNullOrEmpty(i.ReferredBy))
            .GroupBy(i => NormalizeKey(i.ReferredBy!))
            .ToDictionary(g => g.Key, g => (DisplayName: g.First().ReferredBy!.Trim(), Revenue: g.Sum(x => x.TotalAmount)));

        var doctorCommissions = commissionData
            .Where(c => !string.IsNullOrEmpty(c.ReferrerName))
            .GroupBy(c => NormalizeKey(c.ReferrerName!))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.CommissionAmount));

        var result = new List<PhysicianRoiDto>();
        foreach (var doc in doctorRevenue)
        {
            doctorCommissions.TryGetValue(doc.Key, out var comm);
            result.Add(new PhysicianRoiDto { DoctorName = doc.Value.DisplayName, BilledRevenue = doc.Value.Revenue, CommissionPaid = comm });
        }

        return result.OrderByDescending(r => r.BilledRevenue).ToList();
    }
}
