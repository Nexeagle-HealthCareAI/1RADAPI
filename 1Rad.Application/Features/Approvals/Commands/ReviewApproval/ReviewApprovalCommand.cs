using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Common;
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
    // How money goes back when the approved change refunds the patient
    // (CASH / UPI / CARD, or ADVANCE to hold it in the wallet). Default: ADVANCE.
    public string? RefundMode { get; init; }
}

public class ReviewApprovalCommandHandler : IRequestHandler<ReviewApprovalCommand, bool>
{
    private readonly IApplicationDbContext _context;
    private readonly IMediator _mediator;
    private string _refundMode = PaymentLedger.WalletMode;

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
            throw new _1Rad.Domain.Exceptions.NotFoundException("Approval request not found.");
        if (req.Status != "PENDING")
            throw new _1Rad.Domain.Exceptions.BusinessRuleViolationException(
                $"This request was already {req.Status.ToLowerInvariant()}.", "APPROVAL_ALREADY_REVIEWED");

        // Money-affecting requests need a second person (review R18). A centre with a
        // single admin can still approve its own requests, so nothing gets stuck.
        if (request.Approve && req.RequestedBy == _context.UserContext.UserId)
        {
            var me = _context.UserContext.UserId;
            var anotherAdmin = await _context.UserHospitalMappings.AnyAsync(m =>
                m.HospitalId == hospitalId && m.UserId != me
                && m.Roles.Any(r => r.RoleName == _1Rad.Domain.Constants.RoleConstants.AdminOperator
                                 || r.RoleName == _1Rad.Domain.Constants.RoleConstants.AdminDoctor), ct);
            if (anotherAdmin)
                throw new _1Rad.Domain.Exceptions.BusinessRuleViolationException(
                    "You raised this request, so another admin needs to approve it.", "APPROVAL_SELF_REVIEW");
        }

        if (request.Approve)
        {
            _refundMode = PaymentLedger.NormaliseMode(request.RefundMode ?? ReadRefundMode(req.Payload));
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

    private static string? ReadRefundMode(string? payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
            foreach (var key in new[] { "refundMode", "RefundMode" })
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        catch (JsonException) { }
        return null;
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
            case "REFERRER_CONTACT":
                await ApplyReferrerContactAsync(req, ct);
                break;
        }
    }

    /// <summary>
    /// A referrer changed their mobile/email from the portal (review R8). Applied
    /// only once someone at the centre has approved it. Payload: { referrerId, contact, email }.
    /// </summary>
    private async Task ApplyReferrerContactAsync(ApprovalRequest req, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Payload) ? "{}" : req.Payload);
        var root = doc.RootElement;
        if (!root.TryGetProperty("referrerId", out var idEl) || !Guid.TryParse(idEl.GetString(), out var referrerId))
            return;
        var referrer = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == referrerId && r.HospitalId == req.HospitalId && r.DeletedAt == null, ct);
        if (referrer == null)
            throw new _1Rad.Domain.Exceptions.NotFoundException("That referrer no longer exists.");
        if (root.TryGetProperty("contact", out var c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
            referrer.Contact = c.GetString()!.Trim();
        if (root.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
            referrer.Email = e.GetString()!.Trim();
        referrer.UpdatedAt = DateTime.UtcNow;
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
            RowVersion = null, // the approver applies it to the visit as it stands now
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
        // Capture BEFORE the extraCharges block below can mutate it — needed by
        // InvoiceTotals.RecomputeGross's fallback branch.
        var originalAdditionalCharges = invoice.AdditionalCharges;

        invoice.CentreDiscount = centre ?? invoice.CentreDiscount;
        invoice.ReferrerDiscount = referrer ?? invoice.ReferrerDiscount;
        invoice.InstitutionalDeduction = deduction ?? invoice.InstitutionalDeduction;

        if (extraCharges != null)
        {
            // A non-null list is authoritative — including an EMPTY list, which
            // means every extra charge was intentionally removed via the edit
            // modal. Gating this on .Any() (as this handler previously did) left
            // old InvoiceExtraCharge rows orphaned in the DB when a user cleared
            // them — the identical bug CollectPaymentCommand/
            // ApplyInvoiceDiscountCommand already fixed; ported here to match.
            var existingCharges = await _context.InvoiceExtraCharges
                .Where(ec => ec.InvoiceId == invoice.Id)
                .ToListAsync(ct);
            if (existingCharges.Count > 0)
                _context.InvoiceExtraCharges.RemoveRange(existingCharges);
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
                }
            }

            // Sum the validated INPUT, not invoice.ExtraCharges (see the identical
            // comment in CollectPaymentCommand), and serialize it in the same JSON
            // shape the frontend itself writes to this field — the previous
            // pipe-joined human string ("Name: 200 | Other: 50") was a THIRD,
            // incompatible format for a column the drawer otherwise treats as JSON.
            var kept = extraCharges.Where(x => x.Amount > 0).ToList();
            invoice.AdditionalCharges = kept.Sum(x => x.Amount);
            invoice.AdditionalChargesReason = JsonSerializer.Serialize(kept,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        // Canonical recompute (Common/InvoiceTotals.cs).
        var totalDiscount = invoice.CentreDiscount + invoice.ReferrerDiscount + invoice.InstitutionalDeduction;
        InvoiceTotals.RecomputeGross(invoice, originalAdditionalCharges);
        InvoiceTotals.ApplyDiscountAndFinalize(invoice, totalDiscount);

        // Referrer commission follows the referrer-discount change — unpaid rows only;
        // when everything is already paid, a separate adjustment row (review R4).
        await ReferrerDiscountCommission.ApplyAsync(
            _context, invoice, oldReferrerDiscount, absorbToCentre,
            $"edit approved (request {req.Id}{(string.IsNullOrWhiteSpace(req.Reason) ? "" : " — " + req.Reason)})", ct);

        // Re-derive status against the new total; anything collected beyond it goes
        // back to the patient (review B7) instead of sitting as a hidden overpayment.
        await PaymentLedger.SettleAfterTotalChangeAsync(_context, invoice,
            $"Bill lowered (approval {req.Id.ToString()[..8]})", ct, _refundMode);

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
        // Review B6: the money goes back as its own refund row (dated today, with the
        // mode and the approver) instead of deleting the original payments, so closed
        // days don't change and wallet-funded money returns to the wallet.
        decimal refunded = 0;
        foreach (var invoice in invoices)
        {
            var collected = await PaymentLedger.CollectedAsync(_context, invoice, ct);
            if (collected > 0.009m)
            {
                await PaymentLedger.RefundAsync(_context, invoice, collected, _refundMode,
                    $"Visit cancelled (approval {req.Id.ToString()[..8]})", ct);
                refunded += collected;
            }
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
                    // Detached — the cancelled original keeps the service id (only one live
                    // commission per appointment service is allowed by migration 89).
                    AppointmentServiceId = null,
                    ReferenceNumber = c.ReferenceNumber,
                    CommissionAmount = -paidAmount,
                    Status = "UNPAID",
                    TransactionDate = cancelNow,
                    ServiceDate = c.ServiceDate,
                    Remarks = $"[Clawback — appointment cancelled; ₹{paidAmount:0.##} was already paid to {c.ReferrerName}. Recoverable from future referrals. Reason: {req.Reason}]",
                });
            }

            // A PAID row stays exactly as paid (review R11): zeroing it understated the
            // cash actually paid out; the clawback above carries the reversal.
            if (!wasPaid)
            {
                c.CommissionAmount = 0;
                c.Status = "Cancelled";
            }
            c.Remarks = (c.Remarks ?? "") +
                $" [Appointment cancelled — patient returned;{(wasPaid ? "" : " commission set to ₹0.")}" +
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
            await ReferralLedger.RecomputeAccumulatedTotal(_context, referrerId, hospitalId, ct);
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
            // Canonical recompute (Common/InvoiceTotals.cs) — prefers the live
            // items sum over the previously-persisted GrossAmount where the old
            // inline fallback here didn't even try. 100% concession (discount ==
            // gross) drives Total to exactly 0 via the same clamp+finalize step
            // every other invoice-total site uses.
            InvoiceTotals.RecomputeGross(invoice, invoice.AdditionalCharges);
            invoice.CentreDiscount = invoice.GrossAmount;   // 100% concession → free test
            invoice.ReferrerDiscount = 0;
            invoice.InstitutionalDeduction = 0;
            InvoiceTotals.ApplyDiscountAndFinalize(invoice, invoice.CentreDiscount);
            invoice.IsFree = true; // distinguishes a free test from a 100% discount in reports
            // Mark every line free too, so the per-service FREE badges stay
            // consistent whether the user frees all-at-once or one-by-one.
            foreach (var it in invoice.Items) it.IsFree = true;

            // Return any collected money as refund rows (review B6) — the bill is
            // now ₹0, so everything collected is an overpayment.
            invoice.Status = "PAID"; // total is 0 → nothing owed
            await PaymentLedger.SettleAfterTotalChangeAsync(_context, invoice,
                $"Marked free (approval {req.Id.ToString()[..8]})", ct, _refundMode);
            PaymentLedger.DeriveStatus(invoice);
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
                var target = origCommission;
                if (isSelfCut || bearer == "BOTH")
                {
                    // Self / shared → no commission and no deficit.
                    target = 0;
                }
                else if (bearer == "REFERRER")
                {
                    // finalCommission = orig − (orig ÷ Σorig) × freeGross.
                    // Single referrer → orig − freeGross = −(freeGross − orig) deficit.
                    target = sumOrigCommission > 0
                        ? Math.Round(origCommission - (origCommission / sumOrigCommission) * freeGross, 2)
                        : 0;
                }
                // CENTRE → referrer keeps the full cut (centre absorbs the whole fee).

                // A PAID row is disbursement history (review R4): leave it as paid and
                // book the difference as its own UNPAID adjustment, netted at the next payout.
                if (ReferrerDiscountCommission.IsPaid(c))
                {
                    var delta = target - origCommission;
                    if (Math.Abs(delta) >= 0.005m)
                        _context.ReferralCommissions.Add(PaidRowAdjustment(c, delta,
                            $"[Free test ({bearer.ToLowerInvariant()}-borne) after payout — ₹{origCommission:0.##} was already paid; adjustment ₹{delta:0.##} via approval {req.Id}]"));
                    c.Remarks = (c.Remarks ?? "") + $" [Free test via approval {req.Id} — already paid, adjusted by a separate row]";
                    c.UpdatedAt = DateTime.UtcNow;
                    continue;
                }
                c.CommissionAmount = target;

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
                    await ReferralLedger.RecomputeAccumulatedTotal(_context, rid, req.HospitalId, ct);
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

        // Concession already given for lines freed earlier, so the clinic's own
        // discount can be kept when this line is freed (review B8).
        var prevFreeTotal = invoice.Items.Where(it => it.IsFree).Sum(it => it.Amount * it.Quantity);
        var clinicDiscount = Math.Max(0m, invoice.CentreDiscount - prevFreeTotal);

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
        // Canonical gross recompute (Common/InvoiceTotals.cs) — note this
        // command's discount is deliberately CentreDiscount + ReferrerDiscount
        // only (no InstitutionalDeduction), matching the bearer rule above; that
        // business rule is preserved as-is, only the gross/clamp/total mechanics
        // are shared.
        InvoiceTotals.RecomputeGross(invoice, invoice.AdditionalCharges);
        var freeTotal = invoice.Items.Where(it => it.IsFree).Sum(it => it.Amount * it.Quantity);

        invoice.CentreDiscount = clinicDiscount + freeTotal;
        var discount = invoice.CentreDiscount + invoice.ReferrerDiscount;
        InvoiceTotals.ApplyDiscountAndFinalize(invoice, discount);

        // Every line free → the whole bill is a free test (back-compat rollup so
        // existing "free test" reports still recognise it).
        invoice.IsFree = invoice.Items.Count > 0 && invoice.Items.All(it => it.IsFree);

        // Re-derive status against the new payable. Money taken for the other lines
        // stays; only what now exceeds the bill goes back (review B7).
        await PaymentLedger.SettleAfterTotalChangeAsync(_context, invoice,
            $"Scan marked free (approval {req.Id.ToString()[..8]})", ct, _refundMode);
        invoice.UpdatedAt = DateTime.UtcNow;

        // 3) Settle the referral cut for THIS line only, per the bearer rule
        //    (CENTRE keeps the cut, BOTH/Self forfeit, REFERRER carries the
        //    fee − commission deficit). Paid lines' commissions are untouched.
        var comm = apptId == null ? null : await _context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.AppointmentServiceId == serviceId
                && c.HospitalId == req.HospitalId && c.DeletedAt == null, ct);
        if (comm != null && ReferrerDiscountCommission.IsPaid(comm)
            && !string.Equals(comm.ReferrerName, "Self", StringComparison.OrdinalIgnoreCase))
        {
            // Already paid out (review R4): keep the paid row, book the change separately.
            var target = bearer == "BOTH" ? 0m : bearer == "REFERRER" ? Math.Round(comm.CommissionAmount - lineGross, 2) : comm.CommissionAmount;
            var delta = target - comm.CommissionAmount;
            if (Math.Abs(delta) >= 0.005m)
                _context.ReferralCommissions.Add(PaidRowAdjustment(comm, delta,
                    $"[Free service ({bearer.ToLowerInvariant()}-borne) after payout — adjustment ₹{delta:0.##} via approval {req.Id}]"));
            comm.Remarks = (comm.Remarks ?? "") + $" [Free service via approval {req.Id} — already paid, adjusted by a separate row]";
            comm.UpdatedAt = DateTime.UtcNow;
        }
        else if (comm != null && !string.Equals(comm.ReferrerName, "Self", StringComparison.OrdinalIgnoreCase))
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
            await ReferralLedger.RecomputeAccumulatedTotal(_context, comm.ReferrerId, req.HospitalId, ct);
        }
    }

    /// <summary>An UNPAID row carrying a change to a commission that was already paid out.</summary>
    private static ReferralCommission PaidRowAdjustment(ReferralCommission paidRow, decimal delta, string remark) => new()
    {
        HospitalId = paidRow.HospitalId,
        ReferrerId = paidRow.ReferrerId,
        ReferrerName = paidRow.ReferrerName,
        Modality = paidRow.Modality,
        AppointmentId = paidRow.AppointmentId,
        AppointmentServiceId = null, // one live commission per service (migration 89) — the paid row keeps it
        ReferenceNumber = paidRow.ReferenceNumber,
        CommissionAmount = delta,
        Status = "UNPAID",
        TransactionDate = DateTime.UtcNow,
        ServiceDate = paidRow.ServiceDate,
        Remarks = remark,
        UpdatedAt = DateTime.UtcNow,
    };

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
        // Only a paid commission can be "un-paid" (review R17); clear who was paid.
        if (!ReferrerDiscountCommission.IsPaid(commission)) return;

        commission.Status = "UNPAID";
        commission.PaymentDate = null;
        commission.PaidBy = null;
        commission.PayeeName = null;
        commission.PayeeContact = null;
        commission.UpdatedBy = _context.UserContext.UserId.ToString();
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

        // A zero correction is valid (for example, an over-calculated cut), but
        // a negative payout must be represented by the dedicated clawback/
        // deficit workflows rather than rewriting this commission row.
        if (amount is < 0)
            throw new _1Rad.Domain.Exceptions.BusinessRuleViolationException("A payout revision cannot set a negative commission amount.");
        // PAID only through the payout screen (review R6), which records the payee and nets deficits.
        if (string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase) && !ReferrerDiscountCommission.IsPaid(commission))
            throw new _1Rad.Domain.Exceptions.BusinessRuleViolationException(
                "Mark commissions as paid from the payout screen, not by editing them.", "COMMISSION_PAY_VIA_PAYOUT");
        if (ReferrerDiscountCommission.IsPaid(commission))
        {
            // A paid row stays as paid; an amount correction becomes its own adjustment row.
            if (amount.HasValue && Math.Abs(amount.Value - commission.CommissionAmount) >= 0.005m)
                _context.ReferralCommissions.Add(PaidRowAdjustment(commission, amount.Value - commission.CommissionAmount,
                    $"[Correction to a paid commission (₹{commission.CommissionAmount:0.##} → ₹{amount.Value:0.##}) via approval {req.Id} — {req.Reason}]"));
            amount = null;
            status = null;
        }
        if (amount.HasValue) commission.CommissionAmount = amount.Value;
        if (!string.IsNullOrWhiteSpace(modality)) commission.Modality = modality;
        if (remarks != null) commission.Remarks = remarks;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.ToUpperInvariant();
            if (s is "UNPAID" or "PAID" or "CANCELLED")
            {
                commission.Status = s;
                commission.PaymentDate = s == "PAID" ? (commission.PaymentDate ?? DateTime.UtcNow) : null;
            }
        }
        commission.Remarks = (commission.Remarks ?? "") + $" [Edited via approval {req.Id} — {req.Reason}]";
        commission.UpdatedAt = DateTime.UtcNow;

        await ReferralLedger.RecomputeAccumulatedTotal(_context, commission.ReferrerId, req.HospitalId, ct);
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
