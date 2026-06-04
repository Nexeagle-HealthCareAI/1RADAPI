using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    string Notes,
    string ReferredBy,
    string? ReferredContact = null,
    string? PatientName = null,
    string? Mobile = null,
    string? PatientAge = null,
    string? PatientGender = null,
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
    string? ReferrerDegree = null
) : IRequest<bool>;


public class UpdateAppointmentCommandHandler : IRequestHandler<UpdateAppointmentCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateAppointmentCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateAppointmentCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _context.Appointments
            .Include(a => a.Patient)
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId, cancellationToken);

        if (appointment == null) return false;

        bool dateChanged = appointment.DateTime.Date != request.DateTime.Date;

        // If the date has changed, hand out a token for the NEW date. Use the
        // atomic counter seeded from the current MAX token (not count+1, which
        // can collide when tokens have gaps) so the new number is always unique
        // — otherwise the unique index UX_Appointments_Hospital_Date_Token would
        // reject the save with a 500 on a date-change edit.
        if (dateChanged)
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
        appointment.ReferredBy = effectiveReferredBy;
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
        if (!string.IsNullOrEmpty(request.PatientName)) appointment.PatientName = request.PatientName;
        if (!string.IsNullOrEmpty(request.Mobile)) appointment.Mobile = request.Mobile;

        // Update the underlying Patient entity as well
        if (appointment.Patient != null)
        {
            if (!string.IsNullOrEmpty(request.PatientName)) appointment.Patient.FullName = request.PatientName;
            if (!string.IsNullOrEmpty(request.Mobile)) appointment.Patient.Mobile = request.Mobile;
            if (!string.IsNullOrEmpty(request.PatientAge)) appointment.Patient.Age = request.PatientAge;
            if (!string.IsNullOrEmpty(request.PatientGender)) appointment.Patient.Gender = request.PatientGender;
        }

        // Load every live AppointmentService row on this visit. We reconcile
        // against this list whether the client sent v1 scalars or v2 Services.
        var existingServices = await _context.AppointmentServices
            .Where(s => s.AppointmentId == appointment.AppointmentId && s.DeletedAt == null)
            .OrderBy(s => s.UpdatedAt)
            .ToListAsync(cancellationToken);

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

            await ReconcileInvoiceItemsAsync(invoice, liveServices, request, cancellationToken);

            // Total gets recomputed from the line items so the invoice stays
            // consistent with the per-service amounts.
            invoice.GrossAmount = invoice.Items.Sum(i => i.Amount * i.Quantity);
            invoice.TotalAmount = invoice.GrossAmount - invoice.DiscountAmount;
            invoice.ReferralCutValue = liveServices.Sum(s => s.ReferralCutValue);

            // Re-derive the payment status from the new total so an edit that
            // adds a service to a settled invoice doesn't keep showing "PAID"
            // with an outstanding balance (and removing a service flips a
            // part-paid bill to PAID when it's now fully covered). Mirrors the
            // canonical rule in CollectPaymentCommand.
            if (invoice.Status != "CANCELLED")
            {
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
            await ReconcileReferralCommissionsAsync(appointment, liveServices, request, effectiveReferredBy, invoice?.InvoiceId, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
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

        // Soft-delete any existing rows the client dropped. We mark rather
        // than hard-delete so any DiagnosticReport / StudyAsset already
        // attached keeps its FK and its history (the FK has ON DELETE SET
        // NULL as a safety net, but soft-delete is the principled path).
        await Task.CompletedTask;
        foreach (var stale in existing)
        {
            if (!kept.Contains(stale.Id))
            {
                stale.DeletedAt = DateTime.UtcNow;
            }
        }

        return live;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Reconcile InvoiceItems against the live service list. We keep
    // existing items where possible (matching by AppointmentServiceId)
    // and add/remove the rest. The Invoice headline totals are recomputed
    // by the caller from the resulting Items collection.
    // ─────────────────────────────────────────────────────────────────────
    private async Task ReconcileInvoiceItemsAsync(
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
        // client removed from the visit during this edit.
        var stale = invoice.Items.Where(i => !keep.Contains(i.Id)).ToList();
        foreach (var s in stale)
        {
            invoice.Items.Remove(s);
        }
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
        CancellationToken cancellationToken)
    {
        var commissions = await _context.ReferralCommissions
            .Where(c => c.AppointmentId == request.AppointmentId)
            .ToListAsync(cancellationToken);

        // No referrer (or a "Self" / walk-in referral) ⇒ wipe any existing
        // commissions to zero (preserves audit trail) and we're done — Self pays
        // no commission, the centre keeps the whole fee.
        // effectiveReferredBy is the referrer we're allowed to apply — it equals
        // the edit's value normally, but stays the ORIGINAL when a paid
        // appointment's referrer change was locked (approval-gated, Scenario 05).
        if (string.IsNullOrEmpty(effectiveReferredBy) ||
            string.Equals(effectiveReferredBy.Trim(), "Self", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var c in commissions) c.CommissionAmount = 0;
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
        // AppointmentServiceId where possible; fall back to adopting an
        // existing orphan commission row for the primary service.
        var commissionsByServiceId = commissions
            .Where(c => c.AppointmentServiceId.HasValue)
            .ToDictionary(c => c.AppointmentServiceId!.Value);

        var orphan = commissions.FirstOrDefault(c => c.AppointmentServiceId == null);
        var keep   = new HashSet<Guid>();

        foreach (var svc in liveServices)
        {
            if (commissionsByServiceId.TryGetValue(svc.Id, out var existing))
            {
                existing.CommissionAmount = svc.ReferralCutValue;
                existing.Modality         = svc.Modality;
                existing.ReferrerId       = referrer.ReferrerId;
                existing.ReferrerName     = referrer.Name ?? effectiveReferredBy;
                if (!string.IsNullOrEmpty(invoiceDisplayId)) existing.ReferenceNumber = invoiceDisplayId;
                keep.Add(existing.Id);
                continue;
            }

            // Adopt a legacy single-row commission for the primary service
            // so we don't double-up.
            if (orphan != null)
            {
                orphan.AppointmentServiceId = svc.Id;
                orphan.CommissionAmount     = svc.ReferralCutValue;
                orphan.Modality             = svc.Modality;
                orphan.ReferrerId           = referrer.ReferrerId;
                orphan.ReferrerName         = referrer.Name ?? effectiveReferredBy;
                if (!string.IsNullOrEmpty(invoiceDisplayId)) orphan.ReferenceNumber = invoiceDisplayId;
                keep.Add(orphan.Id);
                orphan = null;
                continue;
            }

            if (svc.ReferralCutValue <= 0) continue; // nothing to track

            var newCommission = new ReferralCommission
            {
                ReferrerId           = referrer.ReferrerId,
                ReferrerName         = referrer.Name ?? effectiveReferredBy,
                Modality             = svc.Modality,
                CommissionAmount     = svc.ReferralCutValue,
                Status               = "UNPAID",
                TransactionDate      = DateTime.UtcNow,
                HospitalId           = appointment.HospitalId,
                AppointmentId        = appointment.AppointmentId,
                AppointmentServiceId = svc.Id,
                ReferenceNumber      = invoiceDisplayId
            };
            _context.ReferralCommissions.Add(newCommission);
            // Note: newCommission.Id is generated by Guid.NewGuid() in the
            // entity ctor, so we can keep-by-Id like the others.
            keep.Add(newCommission.Id);
        }

        // Any commission rows that no longer correspond to a live service
        // get their amount zeroed (preserves audit trail, matches v1
        // "drop referrer" behaviour).
        foreach (var c in commissions)
        {
            if (!keep.Contains(c.Id)) c.CommissionAmount = 0;
        }
    }
}
