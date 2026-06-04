using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Approvals.Commands.ReviewApproval;

/// <summary>
/// Admin action on a PENDING request. Approve → applies the change + marks
/// APPROVED; Reject → marks REJECTED (nothing applied). The per-type apply logic
/// is added as each trigger is wired up (EDIT_PAYMENT / CANCEL_APPOINTMENT /
/// CHANGE_REFERRER). Endpoint is role-gated to admin / admin-doctor.
/// </summary>
public record ReviewApprovalCommand : IRequest<bool>
{
    public Guid Id { get; init; }
    public bool Approve { get; init; }
    public string? Note { get; init; }
}

public class ReviewApprovalCommandHandler : IRequestHandler<ReviewApprovalCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public ReviewApprovalCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<bool> Handle(ReviewApprovalCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;

        var req = await _context.ApprovalRequests
            .FirstOrDefaultAsync(a => a.Id == request.Id && a.HospitalId == hospitalId && a.DeletedAt == null, ct);

        if (req == null)
            throw new KeyNotFoundException("Approval request not found.");
        if (req.Status != "PENDING")
            throw new InvalidOperationException($"This request was already {req.Status.ToLowerInvariant()}.");

        if (request.Approve)
        {
            // Apply the requested change atomically with the status update.
            await ApplyApprovedChangeAsync(req, ct);
            req.Status = "APPROVED";
        }
        else
        {
            req.Status = "REJECTED";
        }

        req.ReviewedBy = _context.UserContext.UserId;
        req.ReviewNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        req.ReviewedAt = DateTime.UtcNow;
        req.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Applies an approved request to the underlying records. Each case is wired
    /// up as its trigger is connected (post-payment edit, paid-appointment
    /// cancellation, referrer change on a paid invoice).
    /// </summary>
    private async Task ApplyApprovedChangeAsync(ApprovalRequest req, CancellationToken ct)
    {
        switch (req.Type)
        {
            case "EDIT_PAYMENT":
                await ApplyEditPaymentAsync(req, ct);
                break;
            case "CANCEL_APPOINTMENT":
                await ApplyCancelAppointmentAsync(req, ct);
                break;
            case "CHANGE_REFERRER":
                await ApplyChangeReferrerAsync(req, ct);
                break;
            case "MARK_FREE":
                await ApplyMarkFreeAsync(req, ct);
                break;
        }
    }

    /// <summary>
    /// Re-settles a recorded invoice with corrected discounts. Mirrors the
    /// discount + referrer-commission differential in CollectPayment, then
    /// re-derives the invoice status against the new total. Payload keys
    /// (all optional): centreDiscount, referrerDiscount, deduction.
    /// </summary>
    private async Task ApplyEditPaymentAsync(ApprovalRequest req, CancellationToken ct)
    {
        if (req.InvoiceId == null) return;

        var invoice = await _context.Invoices
            .FirstOrDefaultAsync(i => i.Id == req.InvoiceId && i.HospitalId == req.HospitalId && i.DeletedAt == null, ct);
        if (invoice == null || invoice.Status == "CANCELLED") return;

        var (centre, referrer, deduction) = ReadDiscounts(req.Payload);

        var oldReferrerDiscount = invoice.ReferrerDiscount;

        invoice.CentreDiscount = centre ?? invoice.CentreDiscount;
        invoice.ReferrerDiscount = referrer ?? invoice.ReferrerDiscount;
        invoice.InstitutionalDeduction = deduction ?? invoice.InstitutionalDeduction;

        var totalDiscount = invoice.CentreDiscount + invoice.ReferrerDiscount + invoice.InstitutionalDeduction;
        var gross = invoice.GrossAmount > 0 ? invoice.GrossAmount : invoice.TotalAmount + invoice.DiscountAmount;
        invoice.GrossAmount = gross;
        invoice.DiscountAmount = totalDiscount;
        invoice.TotalAmount = gross - totalDiscount;

        // Referrer-side commission differential (revert old, apply new).
        if (invoice.ReferrerDiscount != oldReferrerDiscount)
        {
            var commission = await _context.ReferralCommissions
                .FirstOrDefaultAsync(c =>
                    (c.AppointmentId == invoice.AppointmentId || (c.ReferenceNumber == invoice.InvoiceId && c.ReferenceNumber != null)) &&
                    c.HospitalId == req.HospitalId, ct);

            if (commission != null)
            {
                commission.CommissionAmount += oldReferrerDiscount; // Revert
                commission.CommissionAmount -= invoice.ReferrerDiscount; // Apply New
                commission.Remarks = (commission.Remarks ?? "") + $" [Edit-approved: ₹{oldReferrerDiscount} -> ₹{invoice.ReferrerDiscount}]";

                // Over-commission concession → carried as a negative (deficit),
                // recovered from the doctor's future referrals. Audit the approval.
                if (commission.CommissionAmount < 0)
                {
                    var deficit = Math.Abs(commission.CommissionAmount);
                    commission.Remarks += $" [DEFICIT ₹{deficit:0.##} via approval {req.Id} — {req.Reason}]";
                }
            }
        }

        // Re-derive status against the new total.
        if (invoice.PaidAmount > 0 && invoice.PaidAmount >= invoice.TotalAmount - 0.01m)
        {
            invoice.Status = "PAID";
            invoice.PaidAt ??= DateTime.UtcNow;
        }
        else if (invoice.PaidAmount > 0)
        {
            invoice.Status = "PARTIAL";
        }
        else
        {
            invoice.Status = "PENDING";
        }

        invoice.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Cancels a PAID appointment after admin sign-off: marks the appointment
    /// CANCELLED, soft-deletes its invoice(s) and referral commission(s) (zeroing
    /// the cut), and re-bases the affected referrers' accumulated totals over
    /// their live commissions. Mirrors UpdateAppointmentStatus' CANCELLED branch —
    /// the difference here is only that a payment had been collected.
    /// </summary>
    private async Task ApplyCancelAppointmentAsync(ApprovalRequest req, CancellationToken ct)
    {
        if (req.AppointmentId == null) return;
        var hospitalId = req.HospitalId;

        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == req.AppointmentId && a.HospitalId == hospitalId, ct);
        if (appointment == null) return;

        var cancelNow = DateTime.UtcNow;

        // Soft-delete invoices (tombstone so the offline cache drops them).
        var invoices = await _context.Invoices
            .Where(i => i.AppointmentId == req.AppointmentId && i.HospitalId == hospitalId && i.DeletedAt == null)
            .ToListAsync(ct);
        var invoiceDisplayIds = invoices.Select(i => i.InvoiceId).ToList();
        foreach (var invoice in invoices)
        {
            invoice.Status = "CANCELLED";
            invoice.DeletedAt = cancelNow;
            invoice.UpdatedAt = cancelNow;
        }

        // Soft-delete + zero the referral commissions.
        var commissions = await _context.ReferralCommissions
            .Where(c => (c.AppointmentId == req.AppointmentId ||
                         (c.ReferenceNumber != null && invoiceDisplayIds.Contains(c.ReferenceNumber))) &&
                        c.HospitalId == hospitalId && c.DeletedAt == null)
            .ToListAsync(ct);
        var referrersToRecalculate = commissions.Select(c => c.ReferrerId).Distinct().ToList();
        foreach (var c in commissions)
        {
            c.DeletedAt = cancelNow;
            c.UpdatedAt = cancelNow;
            c.CommissionAmount = 0;
            c.Status = "Cancelled";
        }

        appointment.Status = "CANCELLED";
        appointment.UpdatedAt = cancelNow;

        // Persist the tombstones so the re-base query below excludes them.
        await _context.SaveChangesAsync(ct);

        // Re-base accumulated totals for affected referrers over their LIVE rows.
        foreach (var referrerId in referrersToRecalculate)
        {
            var remaining = await _context.ReferralCommissions
                .Where(c => c.ReferrerId == referrerId && c.HospitalId == hospitalId && c.DeletedAt == null)
                .OrderBy(c => c.TransactionDate)
                .ToListAsync(ct);
            decimal running = 0;
            foreach (var c in remaining) { running += c.CommissionAmount; c.AccumulatedTotal = running; }
        }
    }

    /// <summary>
    /// Marks the visit as a FREE test after admin sign-off: zeroes the bill and
    /// the referral commission, and reverses any money already collected so there
    /// is no income, no payable and no commission — everything nets to zero (no
    /// negative balance). The gross is kept as a record and fully discounted.
    /// </summary>
    private async Task ApplyMarkFreeAsync(ApprovalRequest req, CancellationToken ct)
    {
        var invoice = req.InvoiceId != null
            ? await _context.Invoices.FirstOrDefaultAsync(i => i.Id == req.InvoiceId && i.HospitalId == req.HospitalId && i.DeletedAt == null, ct)
            : null;
        if (invoice == null && req.AppointmentId != null)
            invoice = await _context.Invoices.FirstOrDefaultAsync(i => i.AppointmentId == req.AppointmentId && i.HospitalId == req.HospitalId && i.DeletedAt == null, ct);

        var apptId = req.AppointmentId ?? invoice?.AppointmentId;

        if (invoice != null && invoice.Status != "CANCELLED")
        {
            var gross = invoice.GrossAmount > 0 ? invoice.GrossAmount : invoice.TotalAmount + invoice.DiscountAmount;
            invoice.GrossAmount = gross;
            invoice.CentreDiscount = gross;   // 100% concession → free test
            invoice.ReferrerDiscount = 0;
            invoice.InstitutionalDeduction = 0;
            invoice.DiscountAmount = gross;
            invoice.TotalAmount = 0;

            // Reverse any collected money so there is no income (Payment has no
            // tombstone column, so the rows are removed outright).
            var payments = await _context.Payments
                .Where(p => p.InvoiceId == invoice.Id && p.HospitalId == req.HospitalId)
                .ToListAsync(ct);
            if (payments.Count > 0) _context.Payments.RemoveRange(payments);

            invoice.PaidAmount = 0;
            invoice.PaidAt = null;
            invoice.Status = "PAID"; // total is 0 → nothing owed, nothing collected
            invoice.UpdatedAt = DateTime.UtcNow;
        }

        if (apptId != null)
        {
            var commissions = await _context.ReferralCommissions
                .Where(c => c.AppointmentId == apptId && c.HospitalId == req.HospitalId && c.DeletedAt == null)
                .ToListAsync(ct);
            var affected = commissions.Select(c => c.ReferrerId).Distinct().ToList();
            foreach (var c in commissions)
            {
                c.CommissionAmount = 0;
                c.Status = "Cancelled";
                c.Remarks = (c.Remarks ?? "") + $" [Free test via approval {req.Id}]";
                c.UpdatedAt = DateTime.UtcNow;
            }

            if (affected.Count > 0)
            {
                await _context.SaveChangesAsync(ct);
                foreach (var rid in affected)
                {
                    var rows = await _context.ReferralCommissions
                        .Where(c => c.ReferrerId == rid && c.HospitalId == req.HospitalId && c.DeletedAt == null)
                        .OrderBy(c => c.TransactionDate)
                        .ToListAsync(ct);
                    decimal running = 0;
                    foreach (var c in rows) { running += c.CommissionAmount; c.AccumulatedTotal = running; }
                }
            }
        }
    }

    /// <summary>
    /// Re-points the appointment's referrer after admin sign-off. Payload keys:
    /// newReferrerName (required), newReferrerContact, newReferrerIsDoctor.
    /// </summary>
    private async Task ApplyChangeReferrerAsync(ApprovalRequest req, CancellationToken ct)
    {
        if (req.AppointmentId == null) return;

        string name; string? contact; bool? isDoctor;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Payload) ? "{}" : req.Payload);
            var root = doc.RootElement;
            name = GetString(root, "newReferrerName") ?? string.Empty;
            contact = GetString(root, "newReferrerContact");
            isDoctor = GetBool(root, "newReferrerIsDoctor");
        }
        catch { return; }

        if (string.IsNullOrWhiteSpace(name)) return;

        await _1Rad.Application.Features.Approvals.ReferrerReassign.ApplyAsync(
            _context, req.AppointmentId.Value, req.HospitalId, name, contact, isDoctor, ct);
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static bool? GetBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return null;
    }

    /// <summary>Reads the three optional discount values from a request payload.</summary>
    private static (decimal? centre, decimal? referrer, decimal? deduction) ReadDiscounts(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
            var root = doc.RootElement;
            return (GetDecimal(root, "centreDiscount"), GetDecimal(root, "referrerDiscount"), GetDecimal(root, "deduction"));
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static decimal? GetDecimal(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var ds)) return ds;
        return null;
    }
}
