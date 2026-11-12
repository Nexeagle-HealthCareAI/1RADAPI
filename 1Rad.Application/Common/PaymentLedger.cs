using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// The one place money leaves a bill (review B5, B6, B7).
///
/// Payment rows are append-only (migration 102 enforces it). Money going back is a
/// separate REFUND row with a negative amount, dated the day it happened, with the
/// mode and the user. So a closed day's cash total never changes after the fact, and
/// the refund itself shows up on the day it was paid out.
///
/// Where the money goes:
///   • The part of the bill that was paid from the patient's advance wallet always
///     goes back to the wallet (a fresh ADVANCE credit), whatever mode was asked for.
///   • The rest goes back as asked: CASH / UPI / CARD (paid out now), or ADVANCE
///     (held in the wallet for a later visit or a later cash-out).
///
/// Invoice.PaidAmount is re-derived as the sum of its payment rows, and the status
/// follows from it (<see cref="DeriveStatus"/>).
/// </summary>
public static class PaymentLedger
{
    public const string WalletMode = "ADVANCE";
    public static readonly IReadOnlyList<string> RefundModes = new[] { "CASH", "UPI", "CARD", WalletMode };

    public sealed record RefundOutcome(decimal Refunded, decimal ToWallet, decimal PaidOut);

    public static string NormaliseMode(string? mode)
    {
        var m = (mode ?? string.Empty).Trim().ToUpperInvariant();
        return RefundModes.Contains(m) ? m : WalletMode;
    }

    /// <summary>Net money on the bill: saved rows plus rows added in this unit of work.</summary>
    public static async Task<List<Payment>> RowsAsync(IApplicationDbContext db, Guid invoiceId, CancellationToken ct)
    {
        var saved = await db.Payments.Where(p => p.InvoiceId == invoiceId).ToListAsync(ct);
        var pending = db.Payments.Local.Where(p => p.InvoiceId == invoiceId && saved.All(s => s.Id != p.Id));
        return saved.Concat(pending).ToList();
    }

    /// <summary>
    /// What has been collected on the bill: the sum of its payment rows. A bill from
    /// before payment rows were kept (legacy sync/import) has none; its stored
    /// PaidAmount is the only record, so that is used.
    /// </summary>
    public static async Task<decimal> CollectedAsync(IApplicationDbContext db, Invoice invoice, CancellationToken ct)
    {
        var rows = await RowsAsync(db, invoice.Id, ct);
        return rows.Count > 0 ? Math.Round(rows.Sum(p => p.Amount), 2) : invoice.PaidAmount;
    }

    /// <summary>
    /// Return <paramref name="amount"/> of what was collected on <paramref name="invoice"/>.
    /// Does not save. Throws when asked to return more than was collected.
    /// </summary>
    public static async Task<RefundOutcome> RefundAsync(
        IApplicationDbContext db, Invoice invoice, decimal amount, string? mode, string reason,
        CancellationToken ct)
    {
        amount = Math.Round(amount, 2);
        if (amount <= 0.009m) return new RefundOutcome(0, 0, 0);
        var rows = await RowsAsync(db, invoice.Id, ct);
        var net = rows.Count > 0 ? rows.Sum(p => p.Amount) : invoice.PaidAmount;
        if (amount > net + 0.009m)
            throw new BusinessRuleViolationException(
                $"Only ₹{net:0.##} has been collected on bill {invoice.InvoiceId}, so ₹{amount:0.##} can't be refunded.",
                "REFUND_EXCEEDS_COLLECTED");

        var requested = NormaliseMode(mode);
        var walletFunded = Math.Max(0m, rows.Where(p => p.PaymentMethod == WalletMode).Sum(p => p.Amount));
        var toWalletAsRestore = Math.Min(amount, walletFunded);
        var rest = amount - toWalletAsRestore;
        var userId = db.UserContext.UserId == Guid.Empty ? (Guid?)null : db.UserContext.UserId;
        var now = DateTime.UtcNow;
        var why = string.IsNullOrWhiteSpace(reason) ? "Refund" : reason.Trim();

        decimal toWallet = 0, paidOut = 0;
        if (toWalletAsRestore > 0.009m)
        {
            AddRefundRow(db, invoice, toWalletAsRestore, WalletMode, $"{why} (advance restored)", userId, now);
            AddWalletCredit(db, invoice, toWalletAsRestore, $"Advance restored — {why}", userId, now);
            toWallet += toWalletAsRestore;
        }
        if (rest > 0.009m)
        {
            AddRefundRow(db, invoice, rest, requested, why, userId, now);
            if (requested == WalletMode)
            {
                AddWalletCredit(db, invoice, rest, $"Held as advance — {why}", userId, now);
                toWallet += rest;
            }
            else paidOut += rest;
        }

        invoice.PaidAmount = Math.Round(net - amount, 2);
        invoice.UpdatedAt = now;
        DeriveStatus(invoice);
        return new RefundOutcome(amount, toWallet, paidOut);
    }

    /// <summary>
    /// After anything lowers the bill: if more was collected than the bill now comes
    /// to, the excess goes back (to the wallet unless a mode is given) and the status
    /// is re-derived. Does not save.
    /// </summary>
    public static async Task<decimal> SettleAfterTotalChangeAsync(
        IApplicationDbContext db, Invoice invoice, string reason, CancellationToken ct, string? mode = null)
    {
        if (string.Equals(invoice.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) return 0;
        var excess = Math.Round(invoice.PaidAmount - invoice.TotalAmount, 2);
        if (excess > 0.009m)
        {
            await RefundAsync(db, invoice, excess, mode ?? WalletMode, reason, ct);
            return excess;
        }
        DeriveStatus(invoice);
        return 0;
    }

    /// <summary>PAID / PARTIAL / PENDING from PaidAmount vs TotalAmount. CANCELLED stays.</summary>
    public static void DeriveStatus(Invoice invoice)
    {
        if (string.Equals(invoice.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) return;
        if (invoice.PaidAmount >= invoice.TotalAmount - 0.01m && (invoice.TotalAmount <= 0.01m || invoice.PaidAmount > 0.009m))
        {
            invoice.Status = "PAID";
            invoice.PaidAt ??= DateTime.UtcNow;
        }
        else if (invoice.PaidAmount > 0.009m)
        {
            invoice.Status = "PARTIAL";
            invoice.PaidAt = null;
        }
        else
        {
            invoice.Status = "PENDING";
            invoice.PaidAt = null;
        }
    }

    private static void AddRefundRow(IApplicationDbContext db, Invoice invoice, decimal amount, string mode, string reason, Guid? userId, DateTime now) =>
        db.Payments.Add(new Payment
        {
            InvoiceId = invoice.Id,
            HospitalId = invoice.HospitalId,
            Amount = -Math.Round(amount, 2),
            Kind = PaymentKinds.Refund,
            PaymentMethod = mode,
            Reason = Truncate(reason, 500),
            CreatedByUserId = userId,
            CreatedAt = now,
        });

    private static void AddWalletCredit(IApplicationDbContext db, Invoice invoice, decimal amount, string remarks, Guid? userId, DateTime now) =>
        db.CreditTransactions.Add(new CreditTransaction
        {
            HospitalId = invoice.HospitalId,
            PatientId = invoice.PatientId,
            PatientName = invoice.PatientName ?? string.Empty,
            Type = "ADVANCE",
            Amount = Math.Round(amount, 2),
            InvoiceId = invoice.Id,
            InvoiceDisplayId = invoice.InvoiceId,
            PaymentMethod = WalletMode,
            CreatedByUserId = userId,
            Remarks = Truncate(remarks, 500),
            CreatedAt = now,
            UpdatedAt = now,
        });

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
