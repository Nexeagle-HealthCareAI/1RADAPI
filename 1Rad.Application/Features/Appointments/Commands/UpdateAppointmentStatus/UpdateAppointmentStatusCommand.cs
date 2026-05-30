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

            // 1. Fetch related invoices, items, and payments
            var invoices = await _context.Invoices
                .Include(i => i.Items)
                .Include(i => i.Payments)
                .Where(i => i.AppointmentId == request.AppointmentId && i.HospitalId == hospitalId)
                .ToListAsync(cancellationToken);

            foreach (var invoice in invoices)
            {
                if (invoice.Payments != null && invoice.Payments.Any())
                {
                    ((DbContext)_context).RemoveRange(invoice.Payments);
                }

                if (invoice.Items != null && invoice.Items.Any())
                {
                    ((DbContext)_context).RemoveRange(invoice.Items);
                }

                _context.Invoices.Remove(invoice);
            }

            // 2. Fetch and remove associated referral commissions
            var invoiceDisplayIds = invoices.Select(i => i.InvoiceId).ToList();
            var commissions = await _context.ReferralCommissions
                .Where(c => (c.AppointmentId == request.AppointmentId || 
                             (c.ReferenceNumber != null && invoiceDisplayIds.Contains(c.ReferenceNumber))) && 
                            c.HospitalId == hospitalId)
                .ToListAsync(cancellationToken);

            var referrersToRecalculate = commissions.Select(c => c.ReferrerId).Distinct().ToList();

            if (commissions.Any())
            {
                _context.ReferralCommissions.RemoveRange(commissions);
            }

            // Save changes to clear the DB state before recalculation
            await _context.SaveChangesAsync(cancellationToken);

            // 3. Cascade recalculation of accumulated commission totals for any affected referrers
            foreach (var referrerId in referrersToRecalculate)
            {
                var allRemainingCommissions = await _context.ReferralCommissions
                    .Where(c => c.ReferrerId == referrerId && c.HospitalId == hospitalId)
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

        // Normalize to uppercase to match backend-set statuses (REPORTED, SCANNED, IN_PROGRESS, etc.)
        appointment.Status = newStatus;
        await _context.SaveChangesAsync(cancellationToken);

        return new UpdateAppointmentStatusResult { Success = true };
    }
}
