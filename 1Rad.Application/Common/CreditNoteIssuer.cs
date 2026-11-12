using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// Raises credit notes and refund vouchers (review B6/B10; see <see cref="CreditNote"/>)
/// from what changed on bills in one save. Called by the DbContext just before it
/// saves, so the documents go in the same transaction as the change.
///
///   • A bill that had money on it (paid or part-paid) and whose value went down —
///     a concession, a dropped service, made free, cancelled or deleted — gets a
///     CREDIT NOTE for the reduction, with any money handed back in the same save.
///   • Money handed back on a bill whose value did not change gets a REFUND VOUCHER.
///     If the bill's value was lowered a moment earlier in the same operation (a
///     separate save), the money is put on that credit note instead.
///
/// A bill lowered before anything was paid is just a revised bill — no document.
/// </summary>
public static class CreditNoteIssuer
{
    public sealed record InvoiceChange(Invoice Invoice, decimal OldTotal, decimal OldPaid, string? OldStatus, DateTime? OldDeletedAt);

    public static async Task<List<CreditNote>> IssueAsync(
        IApplicationDbContext db, IReadOnlyList<InvoiceChange> changes, IReadOnlyList<Payment> addedPayments,
        IReadOnlyList<Invoice> trackedInvoices, DateTime nowUtc, CancellationToken ct)
    {
        var refunds = addedPayments
            .Where(p => p.Kind == PaymentKinds.Refund && p.Amount < 0)
            .GroupBy(p => p.InvoiceId)
            .ToDictionary(g => g.Key, g => new Refund(
                Math.Round(-g.Sum(p => p.Amount), 2),
                string.Join(", ", g.Select(p => p.PaymentMethod).Distinct()),
                g.Select(p => p.Reason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))));

        var drafts = new List<CreditNote>();
        var handled = new HashSet<Guid>();
        foreach (var c in changes)
        {
            var inv = c.Invoice;
            var oldStatus = (c.OldStatus ?? string.Empty).ToUpperInvariant();
            if (oldStatus == "CANCELLED" || c.OldDeletedAt != null) continue;
            if (!(c.OldPaid > 0.009m || oldStatus == "PAID" || oldStatus == "PARTIAL")) continue;

            var gone = string.Equals(inv.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase) || inv.DeletedAt != null;
            var after = gone ? 0m : inv.TotalAmount;
            var reduction = Math.Round(c.OldTotal - after, 2);
            if (reduction <= 0.009m) continue;

            refunds.TryGetValue(inv.Id, out var r);
            var reason = gone
                ? (inv.DeletedAt != null ? "Bill deleted after payment" : "Bill cancelled after payment")
                : after <= 0.009m ? "Bill made free after payment"
                : $"Bill lowered from ₹{c.OldTotal:0.##} to ₹{after:0.##}";
            if (!string.IsNullOrWhiteSpace(r?.Reason)) reason += $" — {r!.Reason}";
            drafts.Add(Draft(db, inv, CreditNoteKinds.CreditNote, reduction, c.OldTotal, after, r?.Total ?? 0, r?.Modes, reason, nowUtc));
            handled.Add(inv.Id);
        }

        foreach (var (invoiceId, r) in refunds)
        {
            if (handled.Contains(invoiceId) || r.Total <= 0.009m) continue;
            var inv = trackedInvoices.FirstOrDefault(i => i.Id == invoiceId)
                      ?? await db.Invoices.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
            if (inv == null) continue;

            var money = r.Total;
            var since = nowUtc.AddMinutes(-2);
            var recent = await db.CreditNotes.IgnoreQueryFilters()
                .Where(n => n.HospitalId == inv.HospitalId && n.InvoiceId == invoiceId && n.Kind == CreditNoteKinds.CreditNote
                            && n.RefundedAmount == 0 && n.CreatedAt >= since)
                .OrderByDescending(n => n.CreatedAt).FirstOrDefaultAsync(ct);
            if (recent != null)
            {
                recent.RefundedAmount = Math.Min(recent.Amount, money);
                recent.RefundModes = Truncate(r.Modes, 100);
                money = Math.Round(money - recent.RefundedAmount, 2);
                if (money <= 0.009m) continue;
            }
            drafts.Add(Draft(db, inv, CreditNoteKinds.RefundVoucher, money, inv.TotalAmount, inv.TotalAmount, money, r.Modes,
                string.IsNullOrWhiteSpace(r.Reason) ? "Money returned on the bill" : r.Reason!, nowUtc));
        }

        if (drafts.Count == 0) return drafts;

        // The centre's admin may have switched credit notes, or GST, off.
        var hospitals = drafts.Select(d => d.HospitalId).Distinct().ToList();
        var settings = await db.HospitalBillingSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(b => hospitals.Contains(b.HospitalId)).ToListAsync(ct);
        drafts.RemoveAll(d => settings.Any(b => b.HospitalId == d.HospitalId && !b.CreditNotesEnabled));
        if (drafts.Count == 0) return drafts;
        var gstOff = settings.Where(b => !b.GstEnabled).Select(b => b.HospitalId).ToHashSet();

        var ids = drafts.Select(d => d.InvoiceId).Distinct().ToList();
        var items = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => ids.Contains(i.Id))
            .SelectMany(i => i.Items.Select(it => new ItemRow(it.InvoiceId, it.Amount, it.Quantity, it.IsFree, it.GstRate)))
            .ToListAsync(ct);
        foreach (var d in drafts)
        {
            if (d.Kind == CreditNoteKinds.CreditNote && !gstOff.Contains(d.HospitalId))
                d.TaxAmount = Math.Round(d.Amount * TaxShare(items.Where(i => i.InvoiceId == d.InvoiceId)), 2);
            d.TaxableValue = Math.Round(d.Amount - d.TaxAmount, 2);

            var fy = InvoiceNumbering.FinancialYearLabel(nowUtc);
            var prefix = d.Kind == CreditNoteKinds.CreditNote ? "CN" : "RV";
            var seq = await db.NextSequenceValueAsync(d.HospitalId, $"{prefix}_FY_{fy}", 1, ct);
            d.FinancialYear = fy;
            d.Number = Format(prefix, fy, seq);
            db.CreditNotes.Add(d);
        }
        return drafts;
    }

    public sealed record ItemRow(Guid InvoiceId, decimal Amount, int Quantity, bool IsFree, decimal? GstRate);
    private sealed record Refund(decimal Total, string Modes, string? Reason);

    /// <summary>"CN" + "2026-27" + 7 → "CN2627-00007" (12 characters; GST caps serials at 16).</summary>
    public static string Format(string prefix, string fyLabel, int seq) => $"{prefix}{InvoiceNumbering.Compact(fyLabel)}-{seq:D5}";

    /// <summary>
    /// Share of a GST-inclusive amount that is tax, in the bill's own mix of rates:
    /// Σ value × r/(100+r) ÷ Σ value over the paid lines.
    /// </summary>
    public static decimal TaxShare(IEnumerable<ItemRow> items)
    {
        var lines = items.Where(i => !i.IsFree).ToList();
        var gross = lines.Sum(i => i.Amount * i.Quantity);
        if (gross <= 0) return 0m;
        return lines.Sum(i => i.Amount * i.Quantity * ((i.GstRate ?? 0m) / (100m + (i.GstRate ?? 0m)))) / gross;
    }

    private static CreditNote Draft(IApplicationDbContext db, Invoice inv, string kind, decimal amount, decimal before, decimal after,
        decimal refunded, string? modes, string reason, DateTime now) => new()
    {
        HospitalId = inv.HospitalId,
        Kind = kind,
        InvoiceId = inv.Id,
        InvoiceNumber = inv.InvoiceId,
        InvoiceDate = inv.CreatedAt,
        PatientId = inv.PatientId,
        PatientName = inv.PatientName,
        Amount = amount,
        RefundedAmount = Math.Min(amount, refunded),
        RefundModes = refunded > 0.009m ? Truncate(modes ?? string.Empty, 100) : null,
        BillTotalBefore = before,
        BillTotalAfter = after,
        Reason = Truncate(reason, 500),
        CreatedByUserId = db.UserContext.UserId == Guid.Empty ? null : db.UserContext.UserId,
        CreatedAt = now,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
