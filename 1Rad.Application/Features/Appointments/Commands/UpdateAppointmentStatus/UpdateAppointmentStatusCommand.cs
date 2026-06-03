using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;

public class UpdateAppointmentStatusResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool NotAllowed { get; set; }
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
                    Message = "Payment has already been collected for this appointment. You are not allowed to cancel it at this time."
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

        // Normalize to uppercase to match backend-set statuses (REPORTED, SCANNED, IN_PROGRESS, etc.)
        appointment.Status = newStatus;
        await _context.SaveChangesAsync(cancellationToken);

        return new UpdateAppointmentStatusResult { Success = true };
    }
}
