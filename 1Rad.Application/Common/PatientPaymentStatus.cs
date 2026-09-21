using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// The single definition of "has the patient paid for the visit that earned this
/// referral commission?" — the gate for paying the referrer out. Previously this
/// was copy-pasted into the commission list, the ledger and the doctor portal
/// (each with slightly different wording), and enforced only in the browser.
/// </summary>
public static class PatientPaymentStatus
{
    public const string Paid = "PAID";
    public const string Partial = "PARTIAL";
    public const string Pending = "PENDING";
    public const string Cancelled = "CANCELLED";

    /// <summary>
    /// Normalises an invoice's collection state into PAID / PARTIAL / PENDING /
    /// CANCELLED. Amounts are the source of truth; the stored status is only a
    /// tie-breaker (a manually settled invoice with no captured amounts, or an
    /// explicit CANCELLED).
    /// </summary>
    public static string Resolve(decimal? paidAmount, decimal? totalAmount, string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized == Cancelled) return Cancelled;

        var paid = paidAmount ?? 0m;
        var total = totalAmount ?? 0m;

        if (total > 0m && paid >= total - 0.01m) return Paid;
        if (paid > 0m) return Partial;

        if (normalized is "PAID" or "COMPLETED" or "SETTLED") return Paid;
        if (normalized == "PARTIAL") return Partial;
        return Pending;
    }

    /// <summary>A referrer can be paid once the patient has paid ANYTHING (full or part).</summary>
    public static bool IsPayable(string? resolvedStatus) =>
        resolvedStatus == Paid || resolvedStatus == Partial;

    /// <summary>
    /// Resolves the patient-payment state for a set of commissions in ONE query.
    /// A commission is matched to its invoice by AppointmentId first, then by the
    /// invoice display id kept in ReferenceNumber; soft-deleted invoices are
    /// ignored (they used to be picked up if they carried the highest PaidAmount).
    /// Commissions with no matching invoice are simply absent from the result.
    /// </summary>
    public static async Task<Dictionary<Guid, string>> ForCommissionsAsync(
        IApplicationDbContext context,
        Guid hospitalId,
        IReadOnlyCollection<(Guid CommissionId, Guid? AppointmentId, string? ReferenceNumber)> commissions,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        if (commissions.Count == 0) return result;

        var apptIds = commissions.Where(c => c.AppointmentId.HasValue).Select(c => c.AppointmentId!.Value).Distinct().ToList();
        var refs = commissions.Where(c => !string.IsNullOrEmpty(c.ReferenceNumber)).Select(c => c.ReferenceNumber!).Distinct().ToList();

        var invoices = await context.Invoices
            .AsNoTracking()
            .Where(i => i.HospitalId == hospitalId && i.DeletedAt == null
                        && ((i.AppointmentId != null && apptIds.Contains(i.AppointmentId.Value))
                            || refs.Contains(i.InvoiceId)))
            .Select(i => new { i.AppointmentId, i.InvoiceId, i.PaidAmount, i.TotalAmount, i.Status })
            .ToListAsync(ct);

        foreach (var c in commissions)
        {
            var match = invoices
                .Where(i => (c.AppointmentId.HasValue && i.AppointmentId == c.AppointmentId)
                            || (!string.IsNullOrEmpty(c.ReferenceNumber) && i.InvoiceId == c.ReferenceNumber))
                .OrderByDescending(i => i.PaidAmount)
                .FirstOrDefault();
            if (match == null) continue;
            result[c.CommissionId] = Resolve(match.PaidAmount, match.TotalAmount, match.Status);
        }
        return result;
    }
}
