using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointment;
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
    private readonly IMediator _mediator;

    public ReviewApprovalCommandHandler(IApplicationDbContext context, IMediator mediator)
    {
        _context = context;
        _mediator = mediator;
    }

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
            case "UNPAY_COMMISSION":
                await ApplyUnpayCommissionAsync(req, ct);
                break;
            case "EDIT_COMMISSION":
                await ApplyEditCommissionAsync(req, ct);
                break;
            case "EDIT_SERVICES":
                await ApplyEditServicesAsync(req, ct);
                break;
        }
    }

    /// <summary>
    /// Re-applies an appointment edit that was blocked inline because it removes a
    /// service whose referral commission was already PAID. The full original edit
    /// payload (the UpdateAppointmentCommand body) is replayed with the internal
    /// ApprovedServiceRemoval flag set — so the paid-commission gate is skipped and
    /// the paid cut is clawed back (negative adjustment) instead of blocking. Any
    /// resulting patient overpayment is returned via the payload's RefundMode
    /// (wallet credit / cash), handled by the same command.
    /// </summary>
    private async Task ApplyEditServicesAsync(ApprovalRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Payload) || req.AppointmentId == null) return;

        UpdateAppointmentCommand? cmd;
        try
        {
            cmd = JsonSerializer.Deserialize<UpdateAppointmentCommand>(
                req.Payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return; // malformed payload — nothing safe to apply
        }
        if (cmd == null) return;

        // Pin to this request's appointment and authorise the privileged path. A
        // missing RefundMode defaults to WALLET so a resulting overpayment is held
        // as patient credit (refundable later) rather than bailing for a prompt.
        cmd = cmd with
        {
            AppointmentId = req.AppointmentId.Value,
            ApprovedServiceRemoval = true,
            RefundMode = string.IsNullOrWhiteSpace(cmd.RefundMode) ? "WALLET" : cmd.RefundMode,
        };

        await _mediator.Send(cmd, ct);
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
            .Include(i => i.Items)
            .Include(i => i.ExtraCharges)
            .FirstOrDefaultAsync(i => i.Id == req.InvoiceId && i.HospitalId == req.HospitalId && i.DeletedAt == null, ct);
        if (invoice == null || invoice.Status == "CANCELLED") return;

        var (centre, referrer, deduction) = ReadDiscounts(req.Payload);
        List<_1Rad.Application.Features.Finance.Commands.ApplyInvoiceDiscount.ExtraChargeDetail>? extraCharges = null;
        
        bool absorbToCentre = false;
        try 
        { 
            using var pd = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Payload) ? "{}" : req.Payload); 
            absorbToCentre = GetBool(pd.RootElement, "absorbExcessToCentre") ?? false; 
            
            if (pd.RootElement.TryGetProperty("extraCharges", out var ecEl) && ecEl.ValueKind == JsonValueKind.Array)
            {
                extraCharges = JsonSerializer.Deserialize<List<_1Rad.Application.Features.Finance.Commands.ApplyInvoiceDiscount.ExtraChargeDetail>>(
                    ecEl.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        } catch { }

        var oldReferrerDiscount = invoice.ReferrerDiscount;

        invoice.CentreDiscount = centre ?? invoice.CentreDiscount;
        invoice.ReferrerDiscount = referrer ?? invoice.ReferrerDiscount;
        invoice.InstitutionalDeduction = deduction ?? invoice.InstitutionalDeduction;

        if (extraCharges != null && extraCharges.Any())
        {
            _context.InvoiceExtraCharges.RemoveRange(invoice.ExtraCharges);
            invoice.ExtraCharges.Clear();
            
            foreach (var ec in extraCharges)
            {
                if (ec.Amount > 0)
                {
                    var newCharge = new InvoiceExtraCharge
                    {
                        InvoiceId = invoice.Id,
                        Reason = string.IsNullOrWhiteSpace(ec.Reason) ? "Extra Charge" : ec.Reason.Trim(),
                        Amount = ec.Amount,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.InvoiceExtraCharges.Add(newCharge);
                    invoice.ExtraCharges.Add(newCharge);
                }
            }
            
            invoice.AdditionalCharges = invoice.ExtraCharges.Sum(x => x.Amount);
            invoice.AdditionalChargesReason = string.Join(" | ", invoice.ExtraCharges.Select(x => $"{x.Reason}: {x.Amount}"));
        }

        // Resolve the commission up-front (needed for both the absorb math and the
        // differential below).
        var commission = invoice.ReferrerDiscount != oldReferrerDiscount
            ? await _context.ReferralCommissions.FirstOrDefaultAsync(c =>
                (c.AppointmentId == invoice.AppointmentId || (c.ReferenceNumber == invoice.InvoiceId && c.ReferenceNumber != null)) &&
                c.HospitalId == req.HospitalId, ct)
            : null;
        var baseCommission = (commission?.CommissionAmount ?? 0) + oldReferrerDiscount;

        // Centre absorbs the over-commission excess → floor the commission at its
        // eligible base and shift the surplus into the centre discount. The patient's
        // total is unchanged (the excess only moves between the two discount buckets).
        if (absorbToCentre && invoice.ReferrerDiscount > baseCommission)
        {
            var excess = invoice.ReferrerDiscount - baseCommission;
            invoice.CentreDiscount += excess;
            invoice.ReferrerDiscount = baseCommission;
        }

        var totalDiscount = invoice.CentreDiscount + invoice.ReferrerDiscount + invoice.InstitutionalDeduction;
        var gross = (invoice.Items?.Sum(i => i.Amount * i.Quantity) ?? 0) + (invoice.AdditionalCharges);
        if (gross <= 0) gross = invoice.GrossAmount > 0 ? invoice.GrossAmount : invoice.TotalAmount + invoice.DiscountAmount;
        invoice.GrossAmount = gross;
        invoice.DiscountAmount = totalDiscount;
        invoice.TotalAmount = gross - totalDiscount;

        // Referrer-side commission differential (revert old, apply new).
        if (commission != null)
        {
            commission.CommissionAmount += oldReferrerDiscount; // Revert
            commission.CommissionAmount -= invoice.ReferrerDiscount; // Apply New
            commission.Remarks = (commission.Remarks ?? "") + $" [Edit-approved: ₹{oldReferrerDiscount} -> ₹{invoice.ReferrerDiscount}]";

            // Over-commission concession kept as a deficit → carried as a negative
            // (recovered from the doctor's future referrals). Audit the approval.
            // (If the centre absorbed the excess above, the commission is now 0.)
            if (commission.CommissionAmount < 0)
            {
                var deficit = Math.Abs(commission.CommissionAmount);
                commission.Remarks += $" [DEFICIT ₹{deficit:0.##} via approval {req.Id} — {req.Reason}]";
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

        // Free → payment: reopening a free bill and charging for it (the edit
        // leaves a payable total) must clear the "free test" flag, so the bill
        // and reports no longer show it as free once money is owed/taken. Only a
        // WHOLE-invoice free sets invoice.IsFree=true; a mixed per-service free
        // leaves it false, so this won't un-free a partially-free visit.
        if (invoice.IsFree && invoice.TotalAmount > 0.01m)
        {
            invoice.IsFree = false;
            foreach (var it in invoice.Items) it.IsFree = false;
            if (invoice.AppointmentId.HasValue)
            {
                var freedServices = await _context.AppointmentServices
                    .Where(s => s.AppointmentId == invoice.AppointmentId.Value && s.HospitalId == req.HospitalId)
                    .ToListAsync(ct);
                foreach (var s in freedServices) { s.IsFree = false; s.UpdatedAt = DateTime.UtcNow; }
            }
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

        // Refund: a cancelled PAID appointment means the patient gets their money
        // back, so any collected payment must be reversed (income nets to zero).
        // Payments have no tombstone column, so the rows are removed outright
        // (mirrors Mark-Free). The refunded total is audited onto the commission.
        var invoiceRowIds = invoices.Select(i => i.Id).ToList();
        decimal refunded = 0;
        if (invoiceRowIds.Count > 0)
        {
            var payments = await _context.Payments
                .Where(p => invoiceRowIds.Contains(p.InvoiceId) && p.HospitalId == hospitalId)
                .ToListAsync(ct);
            refunded = payments.Sum(p => p.Amount);
            if (payments.Count > 0) _context.Payments.RemoveRange(payments);
        }

        // Keep the bill VISIBLE as a cancelled record (do NOT tombstone it) so the
        // Revenue Hub shows what happened instead of the row silently vanishing —
        // marked fully to ₹0 (refunded). The CANCELLED status drives the read-only
        // "refunded · net ₹0" UI; GrossAmount is kept as the original-value record.
        foreach (var invoice in invoices)
        {
            invoice.Status = "CANCELLED";
            invoice.PaidAmount = 0;
            invoice.PaidAt = null;
            invoice.DiscountAmount = invoice.GrossAmount; // fully voided → net 0
            invoice.TotalAmount = 0;
            invoice.UpdatedAt = cancelNow;
        }

        // Zero the referral commissions but keep them VISIBLE (do NOT tombstone)
        // so the Referral Hub shows the cancelled ₹0 row with the reason, mirroring
        // the Revenue Hub, instead of the payout silently disappearing.
        var commissions = await _context.ReferralCommissions
            .Where(c => (c.AppointmentId == req.AppointmentId ||
                         (c.ReferenceNumber != null && invoiceDisplayIds.Contains(c.ReferenceNumber))) &&
                        c.HospitalId == hospitalId && c.DeletedAt == null)
            .ToListAsync(ct);
        var referrersToRecalculate = commissions.Select(c => c.ReferrerId).Distinct().ToList();
        var clawbacks = new List<ReferralCommission>();
        foreach (var c in commissions)
        {
            c.UpdatedAt = cancelNow;

            // If the commission was already PAID, the centre has disbursed money the
            // referrer must now return. Book a clawback (negative, UNPAID) so the
            // balance sheet shows the deficit — recoverable from future referrals —
            // instead of the payout silently vanishing to ₹0. Mirrors the
            // ReferrerReassign paid-path reversal. (item 2)
            var wasPaid = string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase) && c.CommissionAmount > 0;
            var paidAmount = c.CommissionAmount;
            if (wasPaid)
            {
                clawbacks.Add(new ReferralCommission
                {
                    HospitalId = hospitalId,
                    ReferrerId = c.ReferrerId,
                    ReferrerName = c.ReferrerName,
                    Modality = c.Modality,
                    PatientName = c.PatientName,
                    AppointmentId = c.AppointmentId,
                    AppointmentServiceId = c.AppointmentServiceId,
                    ReferenceNumber = c.ReferenceNumber,
                    CommissionAmount = -paidAmount,
                    Status = "UNPAID",
                    TransactionDate = cancelNow,
                    ServiceDate = c.ServiceDate,
                    Remarks = $"[Clawback — appointment cancelled; ₹{paidAmount:0.##} was already paid to {c.ReferrerName}. Recoverable from future referrals. Reason: {req.Reason}]",
                });
            }

            c.CommissionAmount = 0;
            c.Status = "Cancelled";
            c.Remarks = (c.Remarks ?? "") +
                $" [Appointment cancelled — patient returned; commission set to ₹0." +
                (refunded > 0 ? $" Refund ₹{refunded:0.##} to patient." : "") +
                (wasPaid ? $" ₹{paidAmount:0.##} already paid → booked as a clawback deficit against {c.ReferrerName}." : "") +
                $" Reason: {req.Reason}]";
        }
        if (clawbacks.Count > 0) _context.ReferralCommissions.AddRange(clawbacks);

        appointment.Status = "CANCELLED";
        appointment.UpdatedAt = cancelNow;

        // Persist the zeroed commissions so the re-base query below re-reads them.
        await _context.SaveChangesAsync(ct);

        // Re-base accumulated totals for affected referrers over their LIVE rows.
        // The cancelled rows are still live (kept visible) but contribute ₹0, so
        // they don't change anyone's running total.
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
        // Who bears the free test decides the referrer's commission:
        //  CENTRE   → centre absorbs everything; referrer keeps the FULL cut.
        //  BOTH     → centre + referrer share; referrer gets NO commission.
        //  REFERRER → referrer alone bears it; gets NO commission.
        // Self referrals earn nothing regardless of this choice.
        var bearer = "CENTRE";
        Guid? serviceId = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Payload) ? "{}" : req.Payload);
            var b = GetString(doc.RootElement, "bearer");
            if (!string.IsNullOrWhiteSpace(b)) bearer = b.Trim().ToUpperInvariant();
            // Per-service free: when the request names a specific service line,
            // free ONLY that line and leave the rest of the visit payable.
            var sid = GetString(doc.RootElement, "appointmentServiceId");
            if (Guid.TryParse(sid, out var g) && g != Guid.Empty) serviceId = g;
        }
        catch { }
        if (bearer != "REFERRER" && bearer != "BOTH") bearer = "CENTRE";

        if (serviceId.HasValue)
        {
            await ApplyMarkFreePerServiceAsync(req, serviceId.Value, bearer, ct);
            return;
        }

        var invoice = req.InvoiceId != null
            ? await _context.Invoices.Include(i => i.Items).FirstOrDefaultAsync(i => i.Id == req.InvoiceId && i.HospitalId == req.HospitalId && i.DeletedAt == null, ct)
            : null;
        if (invoice == null && req.AppointmentId != null)
            invoice = await _context.Invoices.Include(i => i.Items).FirstOrDefaultAsync(i => i.AppointmentId == req.AppointmentId && i.HospitalId == req.HospitalId && i.DeletedAt == null, ct);

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
            invoice.IsFree = true; // distinguishes a free test from a 100% discount in reports
            // Mark every line free too, so the per-service FREE badges stay
            // consistent whether the user frees all-at-once or one-by-one.
            foreach (var it in invoice.Items) it.IsFree = true;

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
            // Mark every service line on the visit free too (source-of-truth flag),
            // so the whole-visit free is consistent with the per-service path.
            var services = await _context.AppointmentServices
                .Where(s => s.AppointmentId == apptId.Value && s.HospitalId == req.HospitalId && s.DeletedAt == null)
                .ToListAsync(ct);
            foreach (var s in services) { s.IsFree = true; s.UpdatedAt = DateTime.UtcNow; }

            var commissions = await _context.ReferralCommissions
                .Where(c => c.AppointmentId == apptId && c.HospitalId == req.HospitalId && c.DeletedAt == null)
                .ToListAsync(ct);
            var affected = commissions.Select(c => c.ReferrerId).Distinct().ToList();

            // For a REFERRER-borne free test the referrer doesn't just forfeit their
            // cut — they carry the deficit (fee − commission), recovered from future
            // referrals. Spread proportionally to each row's original commission so a
            // single referrer lands exactly on −(fee − commission).
            var freeGross = invoice?.GrossAmount ?? 0m;
            var sumOrigCommission = commissions
                .Where(c => !string.Equals(c.ReferrerName, "Self", StringComparison.OrdinalIgnoreCase))
                .Sum(c => c.CommissionAmount);

            foreach (var c in commissions)
            {
                var isSelfCut = string.Equals(c.ReferrerName, "Self", StringComparison.OrdinalIgnoreCase);
                var origCommission = c.CommissionAmount;
                if (isSelfCut || bearer == "BOTH")
                {
                    // Self / shared → no commission and no deficit.
                    c.CommissionAmount = 0;
                }
                else if (bearer == "REFERRER")
                {
                    // finalCommission = orig − (orig ÷ Σorig) × freeGross.
                    // Single referrer → orig − freeGross = −(freeGross − orig) deficit.
                    c.CommissionAmount = sumOrigCommission > 0
                        ? Math.Round(origCommission - (origCommission / sumOrigCommission) * freeGross, 2)
                        : 0;
                }
                // CENTRE → referrer keeps the full cut (centre absorbs the whole fee).

                if (c.CommissionAmount == 0) c.Status = "Cancelled";
                // A negative balance is an active deficit — keep it live so it nets
                // against the referrer's future commissions (don't cancel it).
                c.Remarks = (c.Remarks ?? "") + $" [Free test — {bearer.ToLowerInvariant()}-borne via approval {req.Id}]";
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
    /// Per-service free test. Frees ONE service line on a multi-service visit and
    /// leaves the rest payable — the opposite of the whole-invoice path above.
    /// The line's gross is kept (recorded), but it's excluded from the payable
    /// total (the centre absorbs it) and its referral cut is settled per the
    /// chosen bearer. Payload: { bearer, appointmentServiceId }.
    /// </summary>
    private async Task ApplyMarkFreePerServiceAsync(ApprovalRequest req, Guid serviceId, string bearer, CancellationToken ct)
    {
        var invoice = req.InvoiceId != null
            ? await _context.Invoices.Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.Id == req.InvoiceId && i.HospitalId == req.HospitalId && i.DeletedAt == null, ct)
            : null;
        if (invoice == null && req.AppointmentId != null)
            invoice = await _context.Invoices.Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.AppointmentId == req.AppointmentId && i.HospitalId == req.HospitalId && i.DeletedAt == null, ct);
        if (invoice == null || invoice.Status == "CANCELLED") return;

        var apptId = req.AppointmentId ?? invoice.AppointmentId;

        // 1) Mark the service line free — the AppointmentService (source of truth)
        //    and its 1:1 InvoiceItem (the billing line).
        var svc = await _context.AppointmentServices
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.HospitalId == req.HospitalId, ct);
        if (svc != null) { svc.IsFree = true; svc.UpdatedAt = DateTime.UtcNow; }

        var line = invoice.Items.FirstOrDefault(it => it.AppointmentServiceId == serviceId);
        decimal lineGross;
        if (line != null)
        {
            line.IsFree = true;
            lineGross = line.Amount * line.Quantity;
        }
        else
        {
            // No matching invoice line (freeform / legacy) — fall back to the
            // service amount so the referral deficit math still has a figure.
            lineGross = svc?.Amount ?? 0m;
        }

        // 2) Recompute the invoice from its lines. The centre absorbs every freed
        //    line (folded into CentreDiscount), so payable = gross − discounts.
        //    Recomputing from scratch (not adding) keeps a re-run idempotent.
        var gross = invoice.Items.Sum(it => it.Amount * it.Quantity);
        if (gross <= 0) gross = invoice.GrossAmount; // legacy invoices with no item rows
        var freeTotal = invoice.Items.Where(it => it.IsFree).Sum(it => it.Amount * it.Quantity);

        invoice.GrossAmount = gross;
        invoice.CentreDiscount = freeTotal;
        var discount = invoice.CentreDiscount + invoice.ReferrerDiscount;
        if (discount > gross) discount = gross;
        invoice.DiscountAmount = discount;
        invoice.TotalAmount = gross - discount;
        if (invoice.TotalAmount < 0) invoice.TotalAmount = 0;

        // Every line free → the whole bill is a free test (back-compat rollup so
        // existing "free test" reports still recognise it).
        invoice.IsFree = invoice.Items.Count > 0 && invoice.Items.All(it => it.IsFree);

        // Re-derive status against the new payable. We do NOT reverse payments —
        // money already taken for the OTHER (paid) lines stays collected.
        if (invoice.PaidAmount >= invoice.TotalAmount) invoice.Status = "PAID";
        else if (invoice.PaidAmount > 0) invoice.Status = "PARTIAL";
        else invoice.Status = "PENDING";
        invoice.UpdatedAt = DateTime.UtcNow;

        // 3) Settle the referral cut for THIS line only, per the bearer rule
        //    (CENTRE keeps the cut, BOTH/Self forfeit, REFERRER carries the
        //    fee − commission deficit). Paid lines' commissions are untouched.
        var comm = apptId == null ? null : await _context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.AppointmentServiceId == serviceId
                && c.HospitalId == req.HospitalId && c.DeletedAt == null, ct);
        if (comm != null && !string.Equals(comm.ReferrerName, "Self", StringComparison.OrdinalIgnoreCase))
        {
            var orig = comm.CommissionAmount;
            if (bearer == "BOTH")
            {
                comm.CommissionAmount = 0;
            }
            else if (bearer == "REFERRER")
            {
                comm.CommissionAmount = Math.Round(orig - lineGross, 2); // deficit if fee > cut
            }
            // CENTRE → referrer keeps the full cut (centre absorbs the fee).

            if (comm.CommissionAmount == 0) comm.Status = "Cancelled";
            comm.Remarks = (comm.Remarks ?? "") + $" [Free service — {bearer.ToLowerInvariant()}-borne via approval {req.Id}]";
            comm.UpdatedAt = DateTime.UtcNow;

            // Rebuild the referrer's running accumulated total (the modified row is
            // the tracked instance, so its new amount is reflected here).
            var rows = await _context.ReferralCommissions
                .Where(c => c.ReferrerId == comm.ReferrerId && c.HospitalId == req.HospitalId && c.DeletedAt == null)
                .OrderBy(c => c.TransactionDate)
                .ToListAsync(ct);
            decimal running = 0;
            foreach (var c in rows) { running += c.CommissionAmount; c.AccumulatedTotal = running; }
        }
    }

    /// <summary>
    /// Reverts a PAID referral commission back to UNPAID after admin sign-off.
    /// Payload: { commissionId }.
    /// </summary>
    private async Task ApplyUnpayCommissionAsync(ApprovalRequest req, CancellationToken ct)
    {
        Guid commissionId = Guid.Empty;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Payload) ? "{}" : req.Payload);
            Guid.TryParse(GetString(doc.RootElement, "commissionId"), out commissionId);
        }
        catch { return; }
        if (commissionId == Guid.Empty) return;

        var commission = await _context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.Id == commissionId && c.HospitalId == req.HospitalId && c.DeletedAt == null, ct);
        if (commission == null) return;

        commission.Status = "UNPAID";
        commission.PaymentDate = null;
        commission.Remarks = (commission.Remarks ?? "") + $" [Reverted to UNPAID via approval {req.Id} — {req.Reason}]";
        commission.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Applies an approved edit to a recorded referral commission (amount /
    /// modality / status / remarks). Payload keys: commissionId (required),
    /// amount, modality, status, remarks. Mirrors the direct PUT it replaces,
    /// but only takes effect after admin sign-off.
    /// </summary>
    private async Task ApplyEditCommissionAsync(ApprovalRequest req, CancellationToken ct)
    {
        Guid commissionId = Guid.Empty;
        decimal? amount = null; string? modality = null; string? status = null; string? remarks = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Payload) ? "{}" : req.Payload);
            var root = doc.RootElement;
            Guid.TryParse(GetString(root, "commissionId"), out commissionId);
            amount = GetDecimal(root, "amount");
            modality = GetString(root, "modality");
            status = GetString(root, "status");
            remarks = GetString(root, "remarks");
        }
        catch { return; }
        if (commissionId == Guid.Empty) return;

        var commission = await _context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.Id == commissionId && c.HospitalId == req.HospitalId && c.DeletedAt == null, ct);
        if (commission == null) return;

        if (amount.HasValue) commission.CommissionAmount = amount.Value;
        if (!string.IsNullOrWhiteSpace(modality)) commission.Modality = modality;
        if (remarks != null) commission.Remarks = remarks;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.ToUpperInvariant();
            commission.Status = s;
            commission.PaymentDate = s == "PAID" ? (commission.PaymentDate ?? DateTime.UtcNow) : null;
        }
        commission.Remarks = (commission.Remarks ?? "") + $" [Edited via approval {req.Id} — {req.Reason}]";
        commission.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Re-points the appointment's referrer after admin sign-off. Payload keys:
    /// newReferrerName (required), newReferrerContact, newReferrerIsDoctor.
    /// </summary>
    private async Task ApplyChangeReferrerAsync(ApprovalRequest req, CancellationToken ct)
    {
        if (req.AppointmentId == null) return;

        string name; string? contact; bool? isDoctor; string? supportedByDoctor;
        string? email; string? specialty; string? degree; string? address;
        string? supportedSpecialty; string? supportedDegree;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Payload) ? "{}" : req.Payload);
            var root = doc.RootElement;
            name = GetString(root, "newReferrerName") ?? string.Empty;
            contact = GetString(root, "newReferrerContact");
            isDoctor = GetBool(root, "newReferrerIsDoctor");
            supportedByDoctor = GetString(root, "newReferrerSupportedByDoctor");
            email = GetString(root, "newReferrerEmail");
            specialty = GetString(root, "newReferrerSpecialty");
            degree = GetString(root, "newReferrerDegree");
            address = GetString(root, "newReferrerAddress");
            supportedSpecialty = GetString(root, "newReferrerSupportedSpecialty");
            supportedDegree = GetString(root, "newReferrerSupportedDegree");
        }
        catch { return; }

        if (string.IsNullOrWhiteSpace(name)) return;

        // Paid path → preserve history with double-entry (reversal + new entry).
        await _1Rad.Application.Features.Approvals.ReferrerReassign.ApplyAsync(
            _context, req.AppointmentId.Value, req.HospitalId, name, contact, isDoctor, ct,
            preserveHistory: true, supportedByDoctor: supportedByDoctor,
            email: email, specialty: specialty, degree: degree, address: address,
            supportedSpecialty: supportedSpecialty, supportedDegree: supportedDegree);
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
