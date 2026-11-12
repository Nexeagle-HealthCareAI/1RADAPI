using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// One place for "the referrer discount on an invoice changed, so the referrer's
/// commission changes by the same amount" (review R2, R4, R19).
///
/// Before, CollectPayment, the EDIT_PAYMENT approval, the draft-discount save and
/// GenerateInvoice each did (or forgot to do) this differently: a discount saved as a
/// draft never came off the commission, the approval rewrote PAID rows, and when every
/// row was already PAID the discount was silently lost.
///
/// Rules:
///   • PAID rows are never edited. The change is spread over the visit's unpaid rows in
///     proportion to their current amounts (the last row takes the rounding remainder).
///   • When every row is already PAID, a separate UNPAID adjustment row carries the change
///     (negative = to be recovered from the referrer's next payout).
///   • With "absorb excess to centre", a referrer discount larger than the commission pool
///     is capped at the pool and the rest moves to the centre discount.
/// </summary>
public static class ReferrerDiscountCommission
{
    public sealed record Outcome(bool Changed, bool Absorbed, decimal ExcessAbsorbed, int RowsAdjusted, bool AdjustmentRowAdded);

    /// <summary>
    /// Apply the move from <paramref name="oldReferrerDiscount"/> to the invoice's current
    /// <c>ReferrerDiscount</c>. Does not save. When the excess is absorbed, the invoice's
    /// DiscountAmount and TotalAmount are re-derived from its (already final) GrossAmount.
    /// </summary>
    public static async Task<Outcome> ApplyAsync(
        IApplicationDbContext db,
        Invoice invoice,
        decimal oldReferrerDiscount,
        bool absorbExcessToCentre,
        string auditNote,
        CancellationToken ct)
    {
        if (Math.Abs(invoice.ReferrerDiscount - oldReferrerDiscount) < 0.005m)
            return new Outcome(false, false, 0, 0, false);

        var rows = await db.ReferralCommissions
            .Where(c => (c.AppointmentId == invoice.AppointmentId
                         || (c.ReferenceNumber != null && c.ReferenceNumber == invoice.InvoiceId))
                        && c.HospitalId == invoice.HospitalId
                        && c.DeletedAt == null)
            .OrderBy(c => c.TransactionDate)
            .ToListAsync(ct);
        if (rows.Count == 0) return new Outcome(false, false, 0, 0, false);

        var paid = rows.Where(IsPaid).ToList();
        var adjustable = rows.Where(c => !IsPaid(c)).ToList();

        // Pool the referrer could still give up this cycle (unpaid rows + what the old discount already took).
        var currentTotal = adjustable.Sum(c => c.CommissionAmount);
        var baseCommission = currentTotal + oldReferrerDiscount;

        decimal excess = 0;
        var absorbed = false;
        if (absorbExcessToCentre && invoice.ReferrerDiscount > baseCommission && baseCommission >= 0)
        {
            excess = invoice.ReferrerDiscount - baseCommission;
            invoice.CentreDiscount += excess;
            invoice.ReferrerDiscount = baseCommission;
            invoice.DiscountAmount = invoice.CentreDiscount + invoice.ReferrerDiscount + invoice.InstitutionalDeduction;
            invoice.TotalAmount = invoice.GrossAmount - invoice.DiscountAmount;
            absorbed = true;
        }

        // Positive delta = the commission grows (discount reduced); negative = it shrinks.
        var delta = oldReferrerDiscount - invoice.ReferrerDiscount;
        if (Math.Abs(delta) < 0.005m) return new Outcome(false, absorbed, excess, 0, false);

        var stamp = $"referrer discount ₹{oldReferrerDiscount:0.##} → ₹{invoice.ReferrerDiscount:0.##}";
        if (adjustable.Count > 0)
        {
            var newTotal = currentTotal + delta;
            var allocated = 0m;
            for (var i = 0; i < adjustable.Count; i++)
            {
                var c = adjustable[i];
                decimal rowNew;
                if (i == adjustable.Count - 1) rowNew = newTotal - allocated;
                else
                {
                    var share = currentTotal != 0 ? c.CommissionAmount / currentTotal : 1m / adjustable.Count;
                    rowNew = Math.Round(newTotal * share, 2);
                    allocated += rowNew;
                }
                var before = c.CommissionAmount;
                c.CommissionAmount = rowNew;
                c.UpdatedAt = DateTime.UtcNow;
                c.Remarks = Append(c.Remarks, $"[Adj ₹{before:0.##} → ₹{rowNew:0.##}: {stamp}; {auditNote}]");
                if (c.CommissionAmount < 0)
                    c.Remarks = Append(c.Remarks, $"[DEFICIT ₹{Math.Abs(c.CommissionAmount):0.##} to recover from future referrals]");
                if (absorbed) c.Remarks = Append(c.Remarks, $"[Excess ₹{excess:0.##} absorbed by centre]");
            }
            return new Outcome(true, absorbed, excess, adjustable.Count, false);
        }

        // Everything already paid out: book the change as its own UNPAID row so it is
        // netted against (or added to) the referrer's next payout.
        var template = paid[0];
        db.ReferralCommissions.Add(new ReferralCommission
        {
            ReferrerId = template.ReferrerId,
            ReferrerName = template.ReferrerName,
            Modality = template.Modality,
            CommissionAmount = delta,
            Status = "UNPAID",
            TransactionDate = DateTime.UtcNow,
            ServiceDate = template.ServiceDate,
            HospitalId = template.HospitalId,
            AppointmentId = template.AppointmentId,
            ReferenceNumber = invoice.InvoiceId,
            Remarks = $"[Adjustment after payout: {stamp}; {auditNote}]",
            UpdatedAt = DateTime.UtcNow,
        });
        return new Outcome(true, absorbed, excess, 0, true);
    }

    public static bool IsPaid(ReferralCommission c) =>
        string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase);

    private static string Append(string? remarks, string note) =>
        string.IsNullOrEmpty(remarks) ? note : $"{remarks} {note}";
}
