using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Appointments;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.UpdateAppointment;

/// <summary>
/// Edit an appointment.
///
/// Two payload shapes, mirroring CreateAppointmentCommand:
///   v1 (legacy) — scalar Service / Modality / Amount / ReferralCutValue.
///                 Handler rewrites the primary AppointmentService row in
///                 place from those scalars; any non-primary services on
///                 the visit are left alone.
///   v2 (multi)  — Services list with optional Id per line. Reconciler
///                 keeps existing rows where Id matches (preserving their
///                 status, TAT timestamps, and the FKs report / study /
///                 commission rows already point at), inserts new lines
///                 with null Id, and soft-deletes existing rows that
///                 aren't present in the incoming list.
/// </summary>
public record UpdateAppointmentCommand(
    Guid AppointmentId,
    string Service,
    string Modality,
    DateTime DateTime,
    string Doctor,
    string? Notes = null,
    string? ReferredBy = null,
    string? ReferredContact = null,
    string? PatientName = null,
    string? Mobile = null,
    string? PatientAge = null,
    string? PatientGender = null,
    string? Village = null,
    string? Block = null,
    string? District = null,
    string? Address = null,
    string? SourceOfInfo = null,
    decimal? Amount = null,
    decimal? ReferralCutValue = null,
    // Clinical urgency: STAT / URGENT / ROUTINE. Null = leave unchanged.
    // Front desk / doctor can bump a walk-in trauma to STAT post-booking.
    string? Priority = null,
    // Multi-service edit (step 2). When supplied this becomes the source
    // of truth for reconciling AppointmentService rows. v1 callers who
    // leave it null get the scalar-only legacy path: only the visit's
    // "primary" service row is touched.
    IReadOnlyList<AppointmentServiceLine>? Services = null,
    // Referral source profile (payee-first model) — kept in sync on edit so
    // changing the referrer here matches the booking flow. The referrer IS the
    // payee; IsDoctor decides which extra fields apply. NULL = the edit didn't
    // touch the referral type, so leave the existing referrer's type alone
    // (don't accidentally flip an agent back to "doctor").
    bool? ReferrerIsDoctor = null,
    string? ReferrerSupportedByDoctor = null,
    string? ReferrerSupportedSpecialty = null,
    string? ReferrerSupportedDegree = null,
    string? ReferrerEmail = null,
    string? ReferrerSpecialty = null,
    string? ReferrerDegree = null,
    string? ReferrerAddress = null,
    // Reschedule-to-future refund choice. When a PAID visit is moved to a future
    // date its bill is voided and any money collected is returned: "WALLET" parks
    // it as a patient credit (carry-forward / refundable), "CASH" books an
    // immediate cash refund. Null defaults to WALLET. Ignored when nothing's paid.
    string? RefundMode = null,
    // INTERNAL — set ONLY by the approvals review flow (ReviewApproval) once an
    // admin has approved removing a service whose referral commission was already
    // PAID. The public PUT endpoint forces this to false, so a client can't use it
    // to bypass the paid-commission gate. When true: skip that gate, and the paid
    // cut is clawed back (a negative adjustment row) instead of blocking.
    bool ApprovedServiceRemoval = false
) : IRequest<UpdateAppointmentResult>;

/// <summary>
/// Outcome of an appointment edit. Most edits just apply (Success). Two cases
/// pause the edit so the operator/admin can decide first, mirroring the rest of
/// the post-payment policy:
///   • RequiresApproval — a service being REMOVED still has a referral commission
///     that was already PAID OUT to the referrer. Money has left the building to a
///     third party, so (like ChangeReferrer) this is routed through admin approval
///     instead of silently dropping the payout. Nothing is applied.
///   • RequiresRefundChoice — removing/shrinking a service left the bill overpaid
///     (PaidAmount > TotalAmount). The excess belongs to the patient; the operator
///     picks wallet (carry-forward credit) vs cash refund. Returned WITHOUT applying
///     so the UI can prompt, then re-submits with RefundMode set.
/// </summary>
public class UpdateAppointmentResult
{
    public bool Success { get; set; }
    public bool NotFound { get; set; }
    public bool RequiresApproval { get; set; }
    public bool RequiresRefundChoice { get; set; }
    public decimal OverpayAmount { get; set; }
    public string Message { get; set; } = string.Empty;
}


public class UpdateAppointmentCommandHandler : IRequestHandler<UpdateAppointmentCommand, UpdateAppointmentResult>
{
    private readonly IApplicationDbContext _context;

    public UpdateAppointmentCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<UpdateAppointmentResult> Handle(UpdateAppointmentCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _context.Appointments
            .Include(a => a.Patient)
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId, cancellationToken);

        if (appointment == null) return new UpdateAppointmentResult { NotFound = true };

        bool dateChanged = appointment.DateTime.Date != request.DateTime.Date;

        // Re-token on a date change — but ONLY for a visit that already HAS a token,
        // i.e. one that's been marked arrived (CONFIRMED) or beyond. Tokens are
        // minted at arrival and keyed by the day, so:
        //   • Arrived-or-above moved to another day → re-issue the token for the
        //     DESTINATION day so it queues correctly there and can't collide with
        //     that day's existing numbers (the unique index UX_Appointments_Hospital
        //     _Date_Token would otherwise reject the save with a 500).
        //   • Not-yet-arrived booking (no token) → leave it null. It gets its number
        //     when it's marked arrived on its new day; a reschedule must not mint one
        //     early (and a null token never collides on the destination day).
        // The atomic counter is seeded from the current MAX token (not count+1,
        // which can collide when tokens have gaps) so the new number is unique.
        if (dateChanged && appointment.DailyTokenNumber.HasValue)
        {
            var appointmentDate = request.DateTime.Date;
            var maxToken = await _context.Appointments
                .Where(a => a.HospitalId == appointment.HospitalId && a.DateTime.Date == appointmentDate)
                .MaxAsync(a => (int?)a.DailyTokenNumber, cancellationToken) ?? 0;
            appointment.DailyTokenNumber = await _context.NextSequenceValueAsync(
                appointment.HospitalId,
                $"APPOINTMENT_TOKEN_{appointmentDate:yyyy-MM-dd}",
                maxToken + 1,
                cancellationToken);
        }

        // Update Appointment scalar fields. Service/Modality are rewritten
        // below from the resolved "primary" service line so they stay in
        // sync with the child rows.
        // Scenario 05 — the referrer (who the commission credits) may only be
        // changed freely while nothing is paid. Once payment exists, the change
        // must go through admin approval (the dedicated change-referrer flow), so
        // a bulk edit here must NOT silently re-point it. Detect that case and
        // keep the existing referrer for every downstream write.
        var prevReferredBy = appointment.ReferredBy;
        var referrerChanged = !string.Equals((prevReferredBy ?? string.Empty).Trim(),
                                              (request.ReferredBy ?? string.Empty).Trim(),
                                              StringComparison.OrdinalIgnoreCase);
        var referrerLocked = false;
        if (referrerChanged)
        {
            referrerLocked = await _context.Invoices
                .AnyAsync(i => i.AppointmentId == request.AppointmentId && (i.PaidAmount > 0 || i.Status == "PAID" || i.Status == "PARTIAL"), cancellationToken)
                || await _context.Payments
                .AnyAsync(p => p.Invoice.AppointmentId == request.AppointmentId, cancellationToken);
        }
        var effectiveReferredBy = referrerLocked ? (prevReferredBy ?? string.Empty) : (request.ReferredBy ?? string.Empty);

        appointment.DateTime = request.DateTime;
        appointment.Doctor = request.Doctor;
        appointment.Notes = request.Notes;
        appointment.ReferredBy = NameNormalizer.Upper(effectiveReferredBy);
        if (!referrerLocked && request.ReferredContact != null)
            appointment.ReferredContact = request.ReferredContact;

        // Update priority only if the client explicitly sent one. Null leaves
        // the existing value alone so a partial edit can't accidentally
        // downgrade a STAT case to the default.
        if (!string.IsNullOrWhiteSpace(request.Priority))
        {
            var normalised = (request.Priority ?? string.Empty).Trim().ToUpperInvariant();
            if (normalised is "STAT" or "URGENT" or "ROUTINE")
                appointment.Priority = normalised;
        }

        // Update denormalized patient info if provided
        if (!string.IsNullOrEmpty(request.PatientName)) appointment.PatientName = NameNormalizer.Upper(request.PatientName);
        if (!string.IsNullOrEmpty(request.Mobile)) appointment.Mobile = request.Mobile;

        // Update the underlying Patient entity as well
        if (appointment.Patient != null)
        {
            // Null means the caller did not edit this field; an empty string is
            // still an explicit request to clear it.
            if (request.PatientName is not null) appointment.Patient.FullName = NameNormalizer.Upper(request.PatientName);
            if (request.Mobile is not null) appointment.Patient.Mobile = request.Mobile;
            if (request.PatientAge is not null) appointment.Patient.Age = request.PatientAge;
            if (request.PatientGender is not null) appointment.Patient.Gender = request.PatientGender;
            if (request.Address is not null) appointment.Patient.Address = request.Address;
            if (request.Village is not null) appointment.Patient.Village = NameNormalizer.Upper(request.Village);
            if (request.Block is not null) appointment.Patient.Block = NameNormalizer.Upper(request.Block);
            if (request.District is not null) appointment.Patient.District = NameNormalizer.Upper(request.District);
            if (request.SourceOfInfo is not null) appointment.Patient.SourceOfInfo = request.SourceOfInfo;
        }

        // Load every live AppointmentService row on this visit. We reconcile
        // against this list whether the client sent v1 scalars or v2 Services.
        var existingServices = await _context.AppointmentServices
            .Where(s => s.AppointmentId == appointment.AppointmentId && s.DeletedAt == null)
            .OrderBy(s => s.UpdatedAt)
            .ToListAsync(cancellationToken);

        // A disbursed commission is settled ledger history. The service can stay
        // on the appointment, but its economic basis and payee cannot be changed
        // through an ordinary edit; doing so would either rewrite money already
        // paid or create a second commission for the same service.
        var paidCommissions = await _context.ReferralCommissions
            .Where(c => c.AppointmentId == appointment.AppointmentId
                     && c.DeletedAt == null
                     && c.Status == "PAID")
            .ToListAsync(cancellationToken);

        if (paidCommissions.Count > 0 && referrerChanged)
        {
            throw new InvalidOperationException(
                "The referring party cannot be changed because this appointment has a paid referral commission. Use the approved referrer-change workflow.");
        }

        if (request.Services is { Count: > 0 })
        {
            var paidByServiceId = paidCommissions
                .Where(c => c.AppointmentServiceId.HasValue)
                .GroupBy(c => c.AppointmentServiceId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var line in request.Services.Where(l => l.Id.HasValue))
            {
                if (!paidByServiceId.TryGetValue(line.Id!.Value, out var paid)) continue;

                var original = existingServices.First(s => s.Id == line.Id.Value);
                if (line.ReferralCutValue != original.ReferralCutValue)
                {
                    throw new InvalidOperationException(
                        "The referral cut cannot be changed after it has been paid. Create an approved commission adjustment instead.");
                }

                // Keep the paid row associated with the same clinical service.
                // Renaming, repricing, or changing modality remains allowed, but
                // must never create another commission for this service.
                _ = paid;
            }
        }

        // ── Paid-commission gate (decision: route to admin approval) ──────────
        // If this edit REMOVES a service whose referral commission has already been
        // PAID OUT to the referrer, money has left the building to a third party.
        // Dropping that service would silently delete the payout record and leave
        // the referrer overpaid with no deficit. So — like ChangeReferrer once
        // anything is paid — block the edit here and let the UI route it through
        // the Approvals queue. Detected BEFORE any mutation so nothing is touched.
        // Only the multi-service (v2) shape can remove a line; v1 keeps every row.
        // Skipped once an admin has approved the removal (ApprovedServiceRemoval) —
        // the paid cut is then clawed back in ReconcileServicesAsync rather than blocked.
        if (!request.ApprovedServiceRemoval && request.Services is { Count: > 0 })
        {
            var incomingIds = request.Services
                .Where(l => l.Id.HasValue)
                .Select(l => l.Id!.Value)
                .ToHashSet();
            var removedIds = existingServices
                .Where(s => !incomingIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToList();
            if (removedIds.Count > 0)
            {
                var hasPaidCut = await _context.ReferralCommissions.AnyAsync(
                    c => c.AppointmentServiceId != null
                         && removedIds.Contains(c.AppointmentServiceId.Value)
                         && c.Status == "PAID"
                         && c.DeletedAt == null,
                    cancellationToken);
                if (hasPaidCut)
                {
                    return new UpdateAppointmentResult
                    {
                        RequiresApproval = true,
                        Message = "A service you removed already has a referral commission paid out to the referrer. " +
                                  "Removing it needs admin approval."
                    };
                }
            }
        }

        var liveServices = await ReconcileServicesAsync(appointment, request, existingServices, cancellationToken);

        // Re-stamp the parent Appointment's denormalised scalars from the
        // first live service so legacy v1 readers (offline PWA) keep
        // working through the rollout.
        var primary = liveServices.FirstOrDefault();
        if (primary != null)
        {
            appointment.Service = primary.ServiceName;
            appointment.Modality = primary.Modality;
        }

        // ── Reschedule to a FUTURE date: the visit "un-happens" ───────────────
        // A future appointment must carry NO bill (it bills fresh on the new
        // arrival). So when the date moves into the future and a live invoice
        // exists, void it + its lines, reverse the referral commissions, reset
        // arrival, and return any money already collected to the patient's credit
        // ledger — as a WALLET credit (carry-forward / refundable) or a CASH
        // refund, per the operator's choice. Done before (and instead of) the
        // normal reconciliation below.
        var movedToFuture = dateChanged && request.DateTime.Date > DateTime.UtcNow.Date;
        if (movedToFuture)
        {
            var liveInvoice = await _context.Invoices
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.AppointmentId == request.AppointmentId && i.DeletedAt == null, cancellationToken);

            if (liveInvoice != null)
            {
                var paid = liveInvoice.PaidAmount;

                // Void the bill so the future visit is clean (items go with it).
                liveInvoice.Status = "CANCELLED";
                liveInvoice.DeletedAt = DateTime.UtcNow;

                // Reverse the referral commissions — the service hasn't happened.
                // Zero the amount before tombstoning (same convention as every other
                // tombstone site in this file/CollectPaymentCommand) — otherwise a
                // stale nonzero row lingers and gets double-counted into the referral
                // total the next time this appointment re-arrives and re-bills, since
                // reads match by AppointmentId without an amount check.
                var comms = await _context.ReferralCommissions
                    .Where(c => c.AppointmentId == request.AppointmentId && c.DeletedAt == null)
                    .ToListAsync(cancellationToken);
                foreach (var c in comms) { c.CommissionAmount = 0; c.Status = "Cancelled"; c.DeletedAt = DateTime.UtcNow; }

                // Return money already collected into the credit ledger.
                if (paid > 0.009m)
                {
                    var cash = string.Equals(request.RefundMode, "CASH", StringComparison.OrdinalIgnoreCase);
                    _context.CreditTransactions.Add(new CreditTransaction
                    {
                        HospitalId = appointment.HospitalId,
                        PatientId = appointment.PatientId,
                        PatientName = appointment.PatientName ?? string.Empty,
                        Type = cash ? "REFUND" : "ADVANCE",
                        Amount = Math.Round(paid, 2),
                        InvoiceId = cash ? (Guid?)null : liveInvoice.Id,
                        InvoiceDisplayId = liveInvoice.InvoiceId,
                        PaymentMethod = cash ? "CASH" : null,
                        CreatedByUserId = _context.UserContext.UserId,
                        Remarks = cash
                            ? "Cash refund — paid visit moved to a future date"
                            : "Advance held — paid visit moved to a future date",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                }
            }

            // No longer served — reset arrival so it bills fresh on the new date.
            appointment.ArrivedAt = null;
            appointment.ScanStartedAt = null;
            appointment.Status = "BOOKED";

            await _context.SaveChangesAsync(cancellationToken);
            return new UpdateAppointmentResult { Success = true };
        }

        // ── Invoice reconciliation (arrival-gated) ────────────────────────
        // One Invoice per visit; one InvoiceItem per live service line. A bill is
        // only created OR modified once the patient has ARRIVED — a not-yet-arrived
        // edit must touch no bill (no-show protection). After arrival: reconcile the
        // existing LIVE invoice, or create one when there isn't one yet.
        var invoice = await _context.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.AppointmentId == request.AppointmentId && i.DeletedAt == null, cancellationToken);

        if (invoice != null && appointment.ArrivedAt != null)
        {
            if (dateChanged)
            {
                invoice.CreatedAt = request.DateTime;
            }

            // Reconcile the lines and learn how much "free" concession left with
            // any REMOVED free service — so its discount doesn't linger (Gap 3).
            var removedFreeTotal = await ReconcileInvoiceItemsAsync(invoice, liveServices, request, cancellationToken);

            // A removed free line must stop discounting the bill: peel its value
            // out of the centre concession + the headline discount. Without this
            // the stale discount would understate the total and manufacture a
            // phantom overpayment / under-charge the surviving payable services.
            if (removedFreeTotal > 0.009m)
            {
                invoice.CentreDiscount = Math.Max(0, invoice.CentreDiscount - removedFreeTotal);
                invoice.DiscountAmount = Math.Max(0, invoice.DiscountAmount - removedFreeTotal);
            }

            // Total gets recomputed from the line items so the invoice stays
            // consistent with the per-service amounts.
            invoice.GrossAmount = invoice.Items.Sum(i => i.Amount * i.Quantity);
            // Discount can't exceed the (possibly reduced) gross.
            if (invoice.DiscountAmount > invoice.GrossAmount) invoice.DiscountAmount = invoice.GrossAmount;
            invoice.TotalAmount = invoice.GrossAmount - invoice.DiscountAmount;
            invoice.ReferralCutValue = liveServices.Sum(s => s.ReferralCutValue);
            invoice.PatientName = appointment.PatientName ?? invoice.PatientName;
            // Keep the free rollup honest: only a bill whose every surviving line
            // is free is still a "free test".
            invoice.IsFree = invoice.Items.Count > 0 && invoice.Items.All(i => i.IsFree);

            // Re-derive the payment status from the new total so an edit that
            // adds a service to a settled invoice doesn't keep showing "PAID"
            // with an outstanding balance (and removing a service flips a
            // part-paid bill to PAID when it's now fully covered). Mirrors the
            // canonical rule in CollectPaymentCommand.
            if (invoice.Status != "CANCELLED")
            {
                // Gap 1 — removing/shrinking a paid service can leave the bill
                // OVERPAID (PaidAmount > TotalAmount). That excess is the patient's
                // money and must be returned, not silently absorbed. The operator
                // chooses how (wallet credit vs cash refund); if they haven't yet,
                // bail WITHOUT persisting so the UI can prompt and re-submit with
                // RefundMode set. (Reuses the same credit ledger as the
                // overpayment + move-to-future flows.)
                var overpay = invoice.PaidAmount - invoice.TotalAmount;
                if (overpay > 0.01m)
                {
                    if (string.IsNullOrWhiteSpace(request.RefundMode))
                    {
                        return new UpdateAppointmentResult
                        {
                            RequiresRefundChoice = true,
                            OverpayAmount = Math.Round(overpay, 2),
                            Message = "Removing these services leaves the bill overpaid — choose how to return the excess."
                        };
                    }

                    var cash = string.Equals(request.RefundMode, "CASH", StringComparison.OrdinalIgnoreCase);
                    _context.CreditTransactions.Add(new CreditTransaction
                    {
                        HospitalId = appointment.HospitalId,
                        PatientId = appointment.PatientId,
                        PatientName = appointment.PatientName ?? string.Empty,
                        Type = cash ? "REFUND" : "ADVANCE",
                        Amount = Math.Round(overpay, 2),
                        InvoiceId = cash ? (Guid?)null : invoice.Id,
                        InvoiceDisplayId = invoice.InvoiceId,
                        PaymentMethod = cash ? "CASH" : null,
                        CreatedByUserId = _context.UserContext.UserId,
                        Remarks = cash
                            ? "Cash refund — service removed after payment"
                            : "Advance held — service removed after payment",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                    // The excess now lives in the credit ledger — the invoice is
                    // settled exactly to its new total.
                    invoice.PaidAmount = invoice.TotalAmount;
                }

                if (invoice.PaidAmount >= invoice.TotalAmount - 0.01m)
                    invoice.Status = invoice.PaidAmount > 0 ? "PAID" : "PENDING";
                else if (invoice.PaidAmount > 0)
                    invoice.Status = "PARTIAL";
                else
                    invoice.Status = "PENDING";

                if (invoice.Status == "PAID" && invoice.PaidAt == null)
                    invoice.PaidAt = DateTime.UtcNow;
            }
        }
        else if (appointment.ArrivedAt != null)
        {
            // No live invoice yet, but the patient has ARRIVED — e.g. auto-billing
            // was off at arrival, or billable services were only added now via this
            // edit. Generate the bill (PENDING) so it can be settled. We gate on
            // arrival so a not-yet-arrived (potential no-show) edit still produces
            // no bill here — that case is billed on arrival as usual. Commissions
            // are handled by the arrival-gated reconciliation below.
            decimal gross = liveServices.Sum(s => s.Amount);
            if (gross > 0)
            {
                var newInvoice = new Invoice
                {
                    AppointmentId = appointment.AppointmentId,
                    PatientId = appointment.PatientId,
                    PatientName = appointment.PatientName ?? "Unknown",
                    HospitalId = appointment.HospitalId,
                    InvoiceId = $"INV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}",
                    GrossAmount = gross,
                    DiscountAmount = 0,
                    TotalAmount = gross,
                    PaidAmount = 0,
                    Status = "PENDING",
                    ReferralCutValue = liveServices.Sum(s => s.ReferralCutValue),
                    CreatedAt = DateTime.UtcNow,
                    ServiceDate = appointment.DateTime,
                };
                foreach (var s in liveServices)
                {
                    newInvoice.Items.Add(new InvoiceItem
                    {
                        Description = s.ServiceName,
                        Amount = s.Amount,
                        Quantity = 1,
                        AppointmentServiceId = s.Id,
                    });
                }
                _context.Invoices.Add(newInvoice);
                invoice = newInvoice; // let the commission reconciliation below reference it
            }
        }

        // ── Referral commission reconciliation ────────────────────────────
        // The legacy path (single commission per appointment) is the v1
        // shape. v2 fans out one commission per service line. We keep both
        // working by reconciling against the live service list.
        //
        // Billing-on-arrival: commissions only exist once the patient has
        // arrived (UpdateAppointmentStatus generates them then). Editing a
        // not-yet-arrived appointment must NOT create commissions — otherwise
        // a no-show that was edited would still produce a referral payout.
        // After arrival we reconcile normally so service add/remove/price
        // edits flow through to the existing commission rows.
        if (appointment.ArrivedAt != null)
        {
            // Auto-billing parity: arrival keeps a ₹0 commission row per service
            // when auto-billing is on. The edit reconcile must mirror that so a
            // service edited/added here behaves identically to one present at
            // arrival (otherwise Revenue gets a line but Referral doesn't).
            var autoBilling = await _context.Hospitals
                .Where(h => h.HospitalId == appointment.HospitalId)
                .Select(h => (bool?)h.IsAutoBillingEnabled)
                .FirstOrDefaultAsync(cancellationToken) ?? false;
            await ReconcileReferralCommissionsAsync(appointment, liveServices, request, effectiveReferredBy, invoice?.InvoiceId, autoBilling, cancellationToken);
        }

        // Persist. Everything above was read fresh in THIS handler, so any
        // concurrency conflict here is spurious rather than a real lost-update:
        // it's a concurrent save of the same visit (a double-submit) or a
        // transient-retry that re-ran SaveChanges after the first attempt had
        // already committed (the RowVersion is then stale). The editing user's
        // change should win — the rest of this handler is already last-writer-wins
        // — so refresh the conflicting rows' concurrency tokens and re-save instead
        // of bubbling a 409 the user can do nothing about. Bounded to avoid a loop.
        const int maxConcurrencyAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxConcurrencyAttempts)
            {
                foreach (var entry in ex.Entries)
                {
                    var dbValues = await entry.GetDatabaseValuesAsync(cancellationToken);
                    if (dbValues == null)
                        entry.State = EntityState.Detached;          // row was deleted — drop just this change
                    else
                        entry.OriginalValues.SetValues(dbValues);     // refresh token; our edited values still win
                }
            }
        }
        return new UpdateAppointmentResult { Success = true };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Reconcile the AppointmentServices rows against the incoming command.
    // Returns the list of live (non-soft-deleted) rows in insertion order
    // so the caller can derive the "primary" service from index 0.
    // ─────────────────────────────────────────────────────────────────────
    private async Task<List<AppointmentService>> ReconcileServicesAsync(
        Appointment appointment,
        UpdateAppointmentCommand request,
        List<AppointmentService> existing,
        CancellationToken cancellationToken)
    {
        // Build the canonical incoming-line list. v2 callers send Services
        // directly. v1 callers send only scalars — we synthesise a single
        // line and pin it to the existing "primary" service row (the
        // earliest-created one) so we update in place instead of creating
        // a fresh row + soft-deleting all the others.
        List<AppointmentServiceLine> incoming;
        if (request.Services is { Count: > 0 })
        {
            incoming = request.Services
                .Where(l => !string.IsNullOrWhiteSpace(l.ServiceName) || !string.IsNullOrWhiteSpace(l.Modality))
                .Select(l => new AppointmentServiceLine(
                    ServiceName:      (l.ServiceName ?? string.Empty).Trim(),
                    Modality:         (l.Modality ?? string.Empty).Trim().ToUpperInvariant(),
                    Amount:           l.Amount,
                    ReferralCutValue: l.ReferralCutValue,
                    Id:               l.Id,
                    ServiceChargeId:  l.ServiceChargeId))
                .ToList();
        }
        else
        {
            // v1 path: keep every existing line, but re-stamp the primary
            // one from the scalar fields the v1 client sent.
            var primaryId = existing.FirstOrDefault()?.Id;
            incoming = existing
                .Select(e => new AppointmentServiceLine(
                    ServiceName:      e.Id == primaryId ? (request.Service ?? e.ServiceName) : e.ServiceName,
                    Modality:         e.Id == primaryId ? (request.Modality ?? e.Modality)  : e.Modality,
                    Amount:           e.Id == primaryId ? (request.Amount ?? e.Amount)      : e.Amount,
                    ReferralCutValue: e.Id == primaryId ? (request.ReferralCutValue ?? e.ReferralCutValue) : e.ReferralCutValue,
                    Id:               e.Id,
                    ServiceChargeId:  e.ServiceChargeId))
                .ToList();

            // Edge case: legacy appointment that pre-dates migration 57 and
            // somehow still has no service row. Synthesise one from scalars
            // so the rest of the system has something to attach to.
            if (incoming.Count == 0)
            {
                incoming.Add(new AppointmentServiceLine(
                    ServiceName:      (request.Service ?? string.Empty).Trim(),
                    Modality:         (request.Modality ?? string.Empty).Trim().ToUpperInvariant(),
                    Amount:           request.Amount ?? 0,
                    ReferralCutValue: request.ReferralCutValue ?? 0));
            }
        }

        var existingById = existing.ToDictionary(e => e.Id);
        var kept = new HashSet<Guid>();
        var live = new List<AppointmentService>(incoming.Count);

        foreach (var line in incoming)
        {
            if (line.Id is { } id && existingById.TryGetValue(id, out var match))
            {
                // Update existing row in place — preserves its Status, TAT
                // timestamps, and the FK pointers reports / studies /
                // commissions already hold against this Id.
                match.ServiceName      = line.ServiceName;
                match.Modality         = line.Modality;
                match.Amount           = line.Amount;
                match.ReferralCutValue = line.ReferralCutValue;
                match.ServiceChargeId  = line.ServiceChargeId ?? match.ServiceChargeId;
                kept.Add(id);
                live.Add(match);
            }
            else
            {
                var svc = new AppointmentService
                {
                    AppointmentId    = appointment.AppointmentId,
                    ServiceChargeId  = line.ServiceChargeId,
                    ServiceName      = line.ServiceName,
                    Modality         = line.Modality,
                    Amount           = line.Amount,
                    ReferralCutValue = line.ReferralCutValue,
                    Status           = "NOT_STARTED",
                    HospitalId       = appointment.HospitalId
                };
                _context.AppointmentServices.Add(svc);
                live.Add(svc);
            }
        }

        // HARD-DELETE the rows the client dropped (the removed services).
        //
        // The four FKs that point at AppointmentService (InvoiceItem,
        // ReferralCommission, DiagnosticReport, StudyAsset) are all NoAction, so
        // the database REJECTS the delete while any dependent row still exists —
        // we must clear them first. Billing artifacts are safe to remove with the
        // service; clinical artifacts are NOT — a removed service that already has
        // a report or DICOM study would lose that history, so we BLOCK the edit
        // and make the caller deal with the report/scan explicitly.
        var staleServices = existing.Where(s => !kept.Contains(s.Id)).ToList();
        if (staleServices.Count > 0)
        {
            var staleIds = staleServices.Select(s => s.Id).ToList();

            // Guard: refuse to remove a service that has clinical work attached.
            var hasClinicalWork =
                await _context.DiagnosticReports.AnyAsync(
                    r => r.AppointmentServiceId != null && staleIds.Contains(r.AppointmentServiceId.Value), cancellationToken)
                || await _context.StudyAssets.AnyAsync(
                    a => a.AppointmentServiceId != null && staleIds.Contains(a.AppointmentServiceId.Value), cancellationToken);
            if (hasClinicalWork)
                throw new ArgumentException(
                    "Cannot remove a service that already has a report or scan attached. " +
                    "Cancel or reassign its report/scan first, then remove the service.");

            // Settle the referral commissions for the removed services (NoAction FK
            // would otherwise block the delete):
            //   • UNPAID → delete with the service (nothing was disbursed).
            //   • PAID   → the referrer already received this cut for a service we're
            //     now removing. Preserve that PAID row for audit (detach it from the
            //     service so the delete can proceed) and book a CLAWBACK — a negative
            //     UNPAID adjustment — so the referrer's ledger nets the cut back out,
            //     recovered from future referrals. The PAID branch is only reachable
            //     once an admin has approved the removal (the inline gate blocks it).
            var staleCommissions = await _context.ReferralCommissions
                .Where(c => c.AppointmentServiceId != null && staleIds.Contains(c.AppointmentServiceId.Value))
                .ToListAsync(cancellationToken);
            var clawbackNow = DateTime.UtcNow;
            foreach (var c in staleCommissions)
            {
                if (string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase) && c.CommissionAmount != 0)
                {
                    _context.ReferralCommissions.Add(new ReferralCommission
                    {
                        ReferrerId           = c.ReferrerId,
                        ReferrerName         = c.ReferrerName,
                        Modality             = c.Modality,
                        CommissionAmount     = -c.CommissionAmount, // clawback (deficit)
                        Status               = "UNPAID",
                        TransactionDate      = clawbackNow,
                        HospitalId           = appointment.HospitalId,
                        AppointmentId        = appointment.AppointmentId,
                        AppointmentServiceId = null,
                        Remarks              = "Clawback — service removed after commission was paid (admin-approved)",
                    });
                    c.AppointmentServiceId = null; // detach so the service row can delete; keep the PAID row for audit
                    c.Remarks = (c.Remarks ?? string.Empty) + " [service removed after payment — clawback booked]";
                    c.UpdatedAt = clawbackNow;
                }
                else
                {
                    _context.ReferralCommissions.Remove(c);
                }
            }

            _context.AppointmentServices.RemoveRange(staleServices);
        }

        return live;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Reconcile InvoiceItems against the live service list. We keep
    // existing items where possible (matching by AppointmentServiceId)
    // and add/remove the rest. The Invoice headline totals are recomputed
    // by the caller from the resulting Items collection.
    // ─────────────────────────────────────────────────────────────────────
    // Returns the gross value of any REMOVED line items that were marked FREE,
    // so the caller can peel that concession back out of the invoice discount.
    private async Task<decimal> ReconcileInvoiceItemsAsync(
        Invoice invoice,
        List<AppointmentService> liveServices,
        UpdateAppointmentCommand request,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        var itemsByServiceId = invoice.Items
            .Where(i => i.AppointmentServiceId.HasValue)
            .ToDictionary(i => i.AppointmentServiceId!.Value);

        var orphanLegacyItem = invoice.Items.FirstOrDefault(i => i.AppointmentServiceId == null);

        var keep = new HashSet<Guid>();
        foreach (var svc in liveServices)
        {
            if (itemsByServiceId.TryGetValue(svc.Id, out var existingItem))
            {
                existingItem.Description = svc.ServiceName;
                existingItem.Amount      = svc.Amount;
                existingItem.Quantity    = 1;
                keep.Add(existingItem.Id);
                continue;
            }

            // Pre-migration invoice that still has its unattached "single
            // service" item — adopt it for the primary service so we don't
            // accidentally double-bill.
            if (orphanLegacyItem != null)
            {
                orphanLegacyItem.Description           = svc.ServiceName;
                orphanLegacyItem.Amount                = svc.Amount;
                orphanLegacyItem.Quantity              = 1;
                orphanLegacyItem.AppointmentServiceId  = svc.Id;
                keep.Add(orphanLegacyItem.Id);
                orphanLegacyItem = null;
                continue;
            }

            var newItem = new InvoiceItem
            {
                InvoiceId             = invoice.Id,
                Description           = svc.ServiceName,
                Amount                = svc.Amount,
                Quantity              = 1,
                AppointmentServiceId  = svc.Id
            };
            invoice.Items.Add(newItem);
            keep.Add(newItem.Id);
        }

        // Drop anything we didn't keep. These are items for services the
        // client removed from the visit during this edit. Tally the FREE ones so
        // the caller can remove their concession from the discount (Gap 3).
        var stale = invoice.Items.Where(i => !keep.Contains(i.Id)).ToList();
        decimal removedFreeTotal = 0;
        foreach (var s in stale)
        {
            if (s.IsFree) removedFreeTotal += s.Amount * s.Quantity;
            invoice.Items.Remove(s);
            // Explicitly mark for deletion to avoid orphans or EF tracking bugs
            // since InvoiceItem is a standard HasOne/WithMany entity.
            _context.Entry(s).State = Microsoft.EntityFrameworkCore.EntityState.Deleted;
        }
        return removedFreeTotal;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Reconcile ReferralCommission rows against the live service list. One
    // commission per service line with a positive cut. v1-only payloads
    // (no Services list) still take the legacy single-commission path so
    // we don't accidentally fan out a single-service edit.
    // ─────────────────────────────────────────────────────────────────────
    private async Task ReconcileReferralCommissionsAsync(
        Appointment appointment,
        List<AppointmentService> liveServices,
        UpdateAppointmentCommand request,
        string effectiveReferredBy,
        string? invoiceDisplayId,
        bool isAutoBillingEnabled,
        CancellationToken cancellationToken)
    {
        // Load EVERY commission for this visit, INCLUDING tombstones — so a row
        // that was soft-deleted by an earlier edit can be revived (DeletedAt
        // cleared) when its service is live again, instead of staying invisible
        // in the Referral Hub (which filters DeletedAt == null).
        var allCommissions = await _context.ReferralCommissions
            .Where(c => c.AppointmentId == request.AppointmentId)
            .ToListAsync(cancellationToken);

        // SETTLED HISTORY the reconciler must never touch: a PAID payout (real
        // money disbursed) and the clawback/detached rows booked when a paid
        // service was removed under admin approval (see ReconcileServicesAsync).
        // Leaving them in the reconcile set would let the orphan-adoption + the
        // end-of-method tombstone loop zero or re-point them, erasing the deficit.
        bool IsSettledHistory(ReferralCommission c) =>
            string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase)
            || (c.Remarks != null && c.Remarks.Contains("Clawback", StringComparison.OrdinalIgnoreCase))
            || (c.Remarks != null && c.Remarks.Contains("service removed after payment", StringComparison.OrdinalIgnoreCase));
        var commissions = allCommissions.Where(c => !IsSettledHistory(c)).ToList();
        var paidServiceIds = allCommissions
            .Where(c => string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase)
                     && c.AppointmentServiceId.HasValue
                     && c.DeletedAt == null)
            .Select(c => c.AppointmentServiceId!.Value)
            .ToHashSet();

        // No referrer (or a "Self" / walk-in referral) ⇒ wipe any existing
        // commissions to zero (preserves audit trail) and we're done — Self pays
        // no commission, the centre keeps the whole fee.
        // effectiveReferredBy is the referrer we're allowed to apply — it equals
        // the edit's value normally, but stays the ORIGINAL when a paid
        // appointment's referrer change was locked (approval-gated, Scenario 05).
        if (string.IsNullOrEmpty(effectiveReferredBy) ||
            string.Equals(effectiveReferredBy.Trim(), "Self", StringComparison.OrdinalIgnoreCase))
        {
            // Self / walk-in pays no commission — drop the rows from the ledger
            // (tombstone), don't leave visible ₹0 entries.
            var nowSelf = DateTime.UtcNow;
            foreach (var c in commissions)
            {
                c.CommissionAmount = 0;
                c.Status = "Cancelled";
                if (c.DeletedAt == null) { c.DeletedAt = nowSelf; c.UpdatedAt = nowSelf; }
            }
            return;
        }

        // Resolve referrer (create on first use).
        var searchName = effectiveReferredBy.Trim();
        var referrer = await _context.Referrers
            .FirstOrDefaultAsync(r => r.Name.ToLower() == searchName.ToLower() && r.HospitalId == appointment.HospitalId, cancellationToken);
        if (referrer == null)
        {
            referrer = new Referrer
            {
                Name = searchName,
                Contact = string.Empty,
                Address = string.Empty,
                HospitalId = appointment.HospitalId
            };
            _context.Referrers.Add(referrer);
        }

        // Keep the referral source profile in sync with what the edit sent
        // (payee-first model — same behaviour as booking). The type is only
        // updated when the edit explicitly specified it; profile fields are
        // only overwritten when a non-blank value arrived, so an empty edit
        // field never wipes a saved profile.
        string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        if (request.ReferrerIsDoctor.HasValue)
        {
            referrer.IsDoctor = request.ReferrerIsDoctor.Value;
            referrer.SupportedByDoctor = request.ReferrerIsDoctor.Value
                ? null
                : (Clean(request.ReferrerSupportedByDoctor) ?? referrer.SupportedByDoctor);

            // Per-appointment supporting doctor: a doctor referral clears it; an
            // agent referral stamps THIS visit's chosen doctor (so the same agent
            // can refer for different doctors on different appointments).
            appointment.SupportedByDoctor = request.ReferrerIsDoctor.Value
                ? null
                : (Clean(request.ReferrerSupportedByDoctor) ?? appointment.SupportedByDoctor);
        }
        referrer.Email     = Clean(request.ReferrerEmail)     ?? referrer.Email;
        referrer.Specialty = Clean(request.ReferrerSpecialty) ?? referrer.Specialty;
        referrer.Degree    = Clean(request.ReferrerDegree)    ?? referrer.Degree;
        referrer.Address   = Clean(request.ReferrerAddress)   ?? referrer.Address;

        // Keep the payee's contact fresh too (doctor or agent). Normalise to a
        // 10-digit local number the same way booking does; only overwrite when
        // a non-blank value arrived so an empty edit never wipes a saved number.
        var contactDigits = Clean(request.ReferredContact);
        if (contactDigits != null)
        {
            contactDigits = new string(contactDigits.Where(char.IsDigit).ToArray());
            if (contactDigits.StartsWith("91") && contactDigits.Length == 12) contactDigits = contactDigits.Substring(2);
            else if (contactDigits.StartsWith("0") && contactDigits.Length == 11) contactDigits = contactDigits.Substring(1);
            if (contactDigits.Length > 0) referrer.Contact = contactDigits;
        }

        // Agent payee → ensure the supporting doctor is also a partner doctor,
        // so the profile is stored once and the name is auto-selectable later.
        var isAgentEdit = request.ReferrerIsDoctor.HasValue && !request.ReferrerIsDoctor.Value;
        var supportName = Clean(request.ReferrerSupportedByDoctor);
        if (isAgentEdit && supportName != null)
        {
            var supportDoc = await _context.Referrers
                .FirstOrDefaultAsync(r => r.Name.ToLower() == supportName.ToLower() && r.HospitalId == appointment.HospitalId, cancellationToken);
            if (supportDoc == null)
            {
                supportDoc = new Referrer
                {
                    Name = supportName,
                    Contact = string.Empty,
                    Address = string.Empty,
                    HospitalId = appointment.HospitalId,
                    IsDoctor = true,
                };
                _context.Referrers.Add(supportDoc);
            }
            supportDoc.IsDoctor  = true;
            supportDoc.Specialty = Clean(request.ReferrerSupportedSpecialty) ?? supportDoc.Specialty;
            supportDoc.Degree    = Clean(request.ReferrerSupportedDegree)    ?? supportDoc.Degree;
        }

        bool isMultiServiceEdit = request.Services is { Count: > 0 };

        if (!isMultiServiceEdit)
        {
            // v1 path: one commission per appointment. Preserve the legacy
            // single-row shape so existing UI/reports don't see a sudden
            // explosion of commission rows for what's still semantically
            // a single-service edit.
            decimal finalCut = request.ReferralCutValue ?? 0;
            var existing = commissions.FirstOrDefault();
            if (existing != null)
            {
                existing.CommissionAmount = finalCut;
                existing.Modality         = request.Modality;
                existing.ReferrerId       = referrer.ReferrerId;
                existing.ReferrerName     = referrer.Name ?? effectiveReferredBy;
                if (appointment.DateTime != existing.TransactionDate)
                {
                    existing.TransactionDate = request.DateTime;
                }
                // Pin to the primary service so the dashboard breaks down
                // legacy edits by modality correctly.
                existing.AppointmentServiceId = liveServices.FirstOrDefault()?.Id;
            }
            else if (finalCut > 0)
            {
                _context.ReferralCommissions.Add(new ReferralCommission
                {
                    ReferrerId           = referrer.ReferrerId,
                    ReferrerName         = referrer.Name ?? effectiveReferredBy,
                    Modality             = request.Modality,
                    CommissionAmount     = finalCut,
                    Status               = "UNPAID",
                    TransactionDate      = DateTime.UtcNow,
                    HospitalId           = appointment.HospitalId,
                    AppointmentId        = appointment.AppointmentId,
                    AppointmentServiceId = liveServices.FirstOrDefault()?.Id
                });
            }
            return;
        }

        // v2 path: reconcile one commission per service. Match by
        // AppointmentServiceId (preferring a LIVE row over a tombstone), then
        // fall back to adopting an existing orphan commission row.
        var commissionsByServiceId = commissions
            .Where(c => c.AppointmentServiceId.HasValue)
            .GroupBy(c => c.AppointmentServiceId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.DeletedAt.HasValue).First());

        var orphan = commissions.FirstOrDefault(c => c.AppointmentServiceId == null && c.DeletedAt == null);
        var keep   = new HashSet<Guid>();
        var touchedReferrers = new HashSet<Guid> { referrer.ReferrerId };
        var now = DateTime.UtcNow;

        foreach (var svc in liveServices)
        {
            // Paid commissions are immutable settlement history. They must not
            // be reconsidered as "missing" rows or an edit would append a second
            // unpaid commission for the same service.
            if (paidServiceIds.Contains(svc.Id)) continue;

            // A row should exist when the line earns a cut, OR auto-billing keeps
            // a ₹0 row (mirrors arrival in UpdateAppointmentStatus so an edited /
            // added service behaves identically to one present at arrival).
            var shouldHaveRow = svc.ReferralCutValue > 0 || isAutoBillingEnabled;

            ReferralCommission? existing = null;
            if (commissionsByServiceId.TryGetValue(svc.Id, out var byId)) existing = byId;
            else if (orphan != null) { existing = orphan; orphan = null; }

            if (existing != null)
            {
                existing.ReferrerId           = referrer.ReferrerId;
                existing.ReferrerName         = referrer.Name ?? effectiveReferredBy;
                existing.Modality             = svc.Modality;
                existing.CommissionAmount     = svc.ReferralCutValue;
                existing.AppointmentServiceId = svc.Id;
                if (!string.IsNullOrEmpty(invoiceDisplayId)) existing.ReferenceNumber = invoiceDisplayId;
                existing.UpdatedAt = now;
                touchedReferrers.Add(existing.ReferrerId);

                if (shouldHaveRow)
                {
                    // Revive the row so the Referral Hub shows the (possibly
                    // changed) amount again — fixes the "amount not updating".
                    existing.DeletedAt = null;
                    if (existing.Status == "Cancelled") existing.Status = "UNPAID";
                }
                else
                {
                    // No cut + no auto-billing → drop this line from the ledger so
                    // a swapped/zeroed modality doesn't linger as a stale row.
                    existing.CommissionAmount = 0;
                    existing.Status = "Cancelled";
                    existing.DeletedAt ??= now;
                }
                keep.Add(existing.Id);
                continue;
            }

            if (!shouldHaveRow) continue; // nothing to track for this line

            var newCommission = new ReferralCommission
            {
                ReferrerId           = referrer.ReferrerId,
                ReferrerName         = referrer.Name ?? effectiveReferredBy,
                Modality             = svc.Modality,
                CommissionAmount     = svc.ReferralCutValue,
                Status               = "UNPAID",
                TransactionDate      = now,
                HospitalId           = appointment.HospitalId,
                AppointmentId        = appointment.AppointmentId,
                AppointmentServiceId = svc.Id,
                ReferenceNumber      = invoiceDisplayId
            };
            _context.ReferralCommissions.Add(newCommission);
            keep.Add(newCommission.Id);
        }

        // Tombstone every commission that no longer maps to a live service
        // (removed services AND any leftover orphan), so the referrer is credited
        // ONLY for the services CURRENTLY on the visit. Soft-delete (not hard) for
        // audit + so the offline sync applies the tombstone to the local cache.
        foreach (var c in commissions)
        {
            if (keep.Contains(c.Id)) continue;
            c.CommissionAmount = 0;
            c.Status = "Cancelled";
            if (c.DeletedAt == null)
            {
                c.DeletedAt = now;
                c.UpdatedAt = now;
            }
        }

        // NOTE: the per-row AccumulatedTotal running column isn't recomputed here
        // (it never was on edit). The Referral Hub aggregates from CommissionAmount
        // — the source of truth this method now keeps correct. AccumulatedTotal is
        // re-derived when commissions are next recorded/paid.
    }
}
