using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;

public class UpdateAppointmentStatusResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool NotAllowed { get; set; }
    // True when the action is blocked only because admin sign-off is required
    // (a PAID appointment cancellation) — the UI offers to submit an approval
    // request rather than showing a dead-end lock.
    public bool RequiresApproval { get; set; }
    // The appointment's daily token (assigned on arrival). Returned so the board
    // can show the number the instant the patient is marked arrived, without
    // waiting for the next delta sync to land. Null until the patient arrives.
    public int? DailyTokenNumber { get; set; }
}

public record UpdateAppointmentStatusCommand(Guid AppointmentId, string Status) : IRequest<UpdateAppointmentStatusResult>;

public class UpdateAppointmentStatusCommandHandler : IRequestHandler<UpdateAppointmentStatusCommand, UpdateAppointmentStatusResult>
{
    private readonly IApplicationDbContext _context;

    public UpdateAppointmentStatusCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<UpdateAppointmentStatusResult> Handle(UpdateAppointmentStatusCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId, cancellationToken);

        if (appointment == null)
        {
            return new UpdateAppointmentStatusResult { Success = false, Message = "Appointment not found." };
        }

        var newStatus = request.Status.ToUpperInvariant();

        // Arrival gate: a study can't be advanced (scanning, reporting, etc.)
        // until the patient has actually arrived. Only marking the patient
        // arrived (CONFIRMED) and cancelling are allowed before that — so the
        // technician/doctor boards can't move a no-show through the workflow.
        var ADVANCE_STATUSES = new[] { "IN_PROGRESS", "SCANNED", "REPORTING", "REPORTED", "DELIVERED", "COMPLETED" };
        if (ADVANCE_STATUSES.Contains(newStatus))
        {
            var curStatus = (appointment.Status ?? string.Empty).ToLowerInvariant();
            var notArrivedYet = appointment.ArrivedAt == null &&
                (curStatus == string.Empty || curStatus == "scheduled" || curStatus == "booked" || curStatus == "future");
            if (notArrivedYet)
            {
                return new UpdateAppointmentStatusResult
                {
                    Success = true,
                    NotAllowed = true,
                    Message = "The patient has not arrived yet. Mark the patient as arrived before updating the study status."
                };
            }
        }

        if (newStatus == "CANCELLED")
        {
            // Enforce validation: Check if study is currently being reported or already finalized
            var isReportingOrFinalized = appointment.Status == "REPORTING" || appointment.Status == "REPORTED" || appointment.Status == "DELIVERED";
            if (isReportingOrFinalized)
            {
                return new UpdateAppointmentStatusResult
                {
                    Success = true,
                    NotAllowed = true,
                    Message = "The study is currently in reporting or has already been reported. You cannot cancel this appointment."
                };
            }

            // Enforce validation: Enforce that an appointment can ONLY be cancelled if no payments have been collected
            var hasPayments = await _context.Invoices
                .AnyAsync(i => i.AppointmentId == request.AppointmentId && (i.PaidAmount > 0 || i.Status == "PAID" || i.Status == "PARTIAL"), cancellationToken)
                || await _context.Payments
                .AnyAsync(p => p.Invoice.AppointmentId == request.AppointmentId, cancellationToken);

            if (hasPayments)
            {
                return new UpdateAppointmentStatusResult
                {
                    Success = true,
                    NotAllowed = true,
                    RequiresApproval = true,
                    Message = "Payment has already been collected for this appointment. Cancelling it needs admin approval."
                };
            }

            var hospitalId = appointment.HospitalId;
            var cancelNow = DateTime.UtcNow;

            // 1. SOFT-delete the related invoices. A HARD delete vanishes from
            //    the DB and is therefore never returned as a tombstone on the
            //    next delta sync — so the offline-first billing page would keep
            //    showing the cancelled appointment's invoice forever. Setting
            //    DeletedAt + UpdatedAt makes the sync engine remove it from the
            //    local cache on the next pull.
            var invoices = await _context.Invoices
                .Where(i => i.AppointmentId == request.AppointmentId && i.HospitalId == hospitalId && i.DeletedAt == null)
                .ToListAsync(cancellationToken);

            var invoiceDisplayIds = invoices.Select(i => i.InvoiceId).ToList();
            foreach (var invoice in invoices)
            {
                invoice.DeletedAt = cancelNow;
                invoice.UpdatedAt = cancelNow;
            }

            // 2. SOFT-delete the associated referral commissions (same tombstone
            //    reasoning — otherwise the Referral Hub keeps showing them) and
            //    zero the amount so any aggregate that ignores the tombstone
            //    still nets to nothing.
            var commissions = await _context.ReferralCommissions
                .Where(c => (c.AppointmentId == request.AppointmentId ||
                             (c.ReferenceNumber != null && invoiceDisplayIds.Contains(c.ReferenceNumber))) &&
                            c.HospitalId == hospitalId && c.DeletedAt == null)
                .ToListAsync(cancellationToken);

            var referrersToRecalculate = commissions.Select(c => c.ReferrerId).Distinct().ToList();
            foreach (var c in commissions)
            {
                c.DeletedAt = cancelNow;
                c.UpdatedAt = cancelNow;
                c.CommissionAmount = 0;
            }

            await _context.SaveChangesAsync(cancellationToken);

            // 3. Recompute accumulated totals for affected referrers over their
            //    LIVE (non-deleted) commissions so the ledger doesn't drift.
            foreach (var referrerId in referrersToRecalculate)
            {
                var allRemainingCommissions = await _context.ReferralCommissions
                    .Where(c => c.ReferrerId == referrerId && c.HospitalId == hospitalId && c.DeletedAt == null)
                    .OrderBy(c => c.TransactionDate)
                    .ToListAsync(cancellationToken);

                decimal runningTotal = 0;
                foreach (var c in allRemainingCommissions)
                {
                    runningTotal += c.CommissionAmount;
                    c.AccumulatedTotal = runningTotal;
                }
            }

            if (referrersToRecalculate.Any())
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        // Turnaround-time milestones — set only on the FIRST transition into
        // each status. The null-guard means a status correction (e.g. tech
        // bumps back to CONFIRMED) doesn't reset the arrival clock, and a
        // double-click of "Mark Arrived" doesn't move the timestamp forward.
        // UtcNow keeps the DB timezone-clean; the frontend renders in
        // Asia/Kolkata like the rest of the app.
        var nowUtc = DateTime.UtcNow;
        if (newStatus == "CONFIRMED"   && appointment.ArrivedAt     == null) appointment.ArrivedAt     = nowUtc;
        if (newStatus == "IN_PROGRESS" && appointment.ScanStartedAt == null) appointment.ScanStartedAt = nowUtc;

        // Generate the daily token on ARRIVAL (first time the patient is marked
        // CONFIRMED / arrived), not at booking — so the number reflects who
        // actually showed up first, in order. Only assign once; a re-confirm or
        // a status correction never re-issues or changes an existing token.
        // The atomic counter (seeded from the current max) guarantees the number
        // is unique for the hospital + day, even with several front desks
        // marking arrivals at the same moment.
        if (newStatus == "CONFIRMED" && !appointment.DailyTokenNumber.HasValue)
        {
            var tokenDate = appointment.DateTime.Date;
            var maxToken = await _context.Appointments
                .Where(a => a.HospitalId == appointment.HospitalId && a.DateTime.Date == tokenDate)
                .MaxAsync(a => (int?)a.DailyTokenNumber, cancellationToken) ?? 0;
            appointment.DailyTokenNumber = await _context.NextSequenceValueAsync(
                appointment.HospitalId,
                $"APPOINTMENT_TOKEN_{tokenDate:yyyy-MM-dd}",
                maxToken + 1,
                cancellationToken);
        }

        // Billing + referral commissions are generated on ARRIVAL — the first
        // time the patient is marked CONFIRMED — not at booking. A no-show is
        // therefore never billed and never pays a referral cut.
        if (newStatus == "CONFIRMED")
        {
            await GenerateBillingOnArrivalAsync(appointment, cancellationToken);
        }

        // Normalize to uppercase to match backend-set statuses (REPORTED, SCANNED, IN_PROGRESS, etc.)
        appointment.Status = newStatus;
        await _context.SaveChangesAsync(cancellationToken);

        return new UpdateAppointmentStatusResult { Success = true, DailyTokenNumber = appointment.DailyTokenNumber };
    }

    // Generate the visit's Invoice (when auto-billing is on) and the per-service
    // referral commissions, from the appointment's current service lines. Mirrors
    // the logic that used to run at booking. Idempotent: a re-confirm or status
    // correction never double-bills, because we bail if a live invoice or any
    // live commission already exists for this appointment.
    private async Task GenerateBillingOnArrivalAsync(Appointment appointment, CancellationToken ct)
    {
        var alreadyBilled = await _context.Invoices
            .AnyAsync(i => i.AppointmentId == appointment.AppointmentId && i.DeletedAt == null, ct);
        var alreadyCommissioned = await _context.ReferralCommissions
            .AnyAsync(c => c.AppointmentId == appointment.AppointmentId && c.DeletedAt == null, ct);
        if (alreadyBilled || alreadyCommissioned) return;

        var services = await _context.AppointmentServices
            .Where(s => s.AppointmentId == appointment.AppointmentId && s.DeletedAt == null)
            .OrderBy(s => s.UpdatedAt)
            .ToListAsync(ct);
        if (services.Count == 0) return;

        var hospital = await _context.Hospitals.FindAsync(new object[] { appointment.HospitalId }, ct);
        bool isAutoBillingEnabled = hospital?.IsAutoBillingEnabled ?? false;

        decimal totalAmount      = services.Sum(s => s.Amount);
        decimal totalReferralCut = services.Sum(s => s.ReferralCutValue);

        // Anchor financial records to the SERVICE date (the scheduled visit),
        // not the arrival timestamp, so dashboards group by when care happened.
        var serviceDate = appointment.DateTime;
        string? invoiceDisplayId = null;

        if (totalAmount > 0 && isAutoBillingEnabled)
        {
            invoiceDisplayId = $"INV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}";
            var invoice = new Invoice
            {
                AppointmentId = appointment.AppointmentId,
                PatientId = appointment.PatientId,
                PatientName = appointment.PatientName ?? "Unknown",
                HospitalId = appointment.HospitalId,
                InvoiceId = invoiceDisplayId,
                GrossAmount = totalAmount,
                DiscountAmount = 0,
                TotalAmount = totalAmount,
                PaidAmount = 0,
                Status = "PENDING",
                ReferralCutValue = totalReferralCut,
                CreatedAt = DateTime.UtcNow,
                ServiceDate = serviceDate,
            };
            foreach (var s in services)
            {
                invoice.Items.Add(new InvoiceItem
                {
                    Description = s.ServiceName,
                    Amount = s.Amount,
                    Quantity = 1,
                    AppointmentServiceId = s.Id
                });
            }
            _context.Invoices.Add(invoice);
        }

        // Resolve the referrer via the patient link set at booking.
        var patient = await _context.Patients
            .FirstOrDefaultAsync(p => p.PatientId == appointment.PatientId, ct);
        Guid? referrerId = patient?.ReferrerId;
        string? referrerName = appointment.ReferredBy;
        if (referrerId != null)
        {
            var refRec = await _context.Referrers
                .FirstOrDefaultAsync(r => r.ReferrerId == referrerId, ct);
            if (refRec?.Name != null) referrerName = refRec.Name;
        }

        // A "Self" / walk-in referral pays NO commission — the centre keeps the
        // whole fee. Skip commission generation entirely in that case.
        var isSelfReferral = string.Equals((appointment.ReferredBy ?? string.Empty).Trim(), "Self", StringComparison.OrdinalIgnoreCase);

        if (referrerId != null && !isSelfReferral && (isAutoBillingEnabled || totalReferralCut > 0))
        {
            var currentTotal = await _context.ReferralCommissions
                .Where(c => c.ReferrerId == referrerId && c.HospitalId == appointment.HospitalId)
                .SumAsync(c => (decimal?)c.CommissionAmount, ct) ?? 0;

            foreach (var s in services)
            {
                if (s.ReferralCutValue <= 0 && !isAutoBillingEnabled) continue;
                currentTotal += s.ReferralCutValue;
                _context.ReferralCommissions.Add(new ReferralCommission
                {
                    ReferrerId = referrerId.Value,
                    ReferrerName = referrerName ?? "Self-Referral",
                    Modality = s.Modality,
                    CommissionAmount = s.ReferralCutValue,
                    AccumulatedTotal = currentTotal,
                    Status = "UNPAID",
                    TransactionDate = DateTime.UtcNow,
                    ServiceDate = serviceDate,
                    HospitalId = appointment.HospitalId,
                    AppointmentId = appointment.AppointmentId,
                    AppointmentServiceId = s.Id,
                    ReferenceNumber = invoiceDisplayId
                });
            }
        }
    }
}
