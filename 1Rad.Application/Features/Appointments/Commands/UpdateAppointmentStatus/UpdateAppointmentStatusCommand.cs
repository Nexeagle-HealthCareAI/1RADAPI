using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;

public record UpdateAppointmentStatusCommand(Guid AppointmentId, string Status) : IRequest<bool>;

public class UpdateAppointmentStatusCommandHandler : IRequestHandler<UpdateAppointmentStatusCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateAppointmentStatusCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateAppointmentStatusCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId, cancellationToken);

        if (appointment == null) return false;

        var newStatus = request.Status.ToUpperInvariant();

        if (newStatus == "CANCELLED")
        {
            var hospitalId = appointment.HospitalId;

            // Airtight business validation: Enforce that an appointment can ONLY be cancelled if no payments have been collected
            var hasPayments = await _context.Invoices
                .AnyAsync(i => i.AppointmentId == request.AppointmentId && (i.PaidAmount > 0 || i.Status == "PAID" || i.Status == "PARTIAL"), cancellationToken)
                || await _context.Payments
                .AnyAsync(p => p.Invoice.AppointmentId == request.AppointmentId, cancellationToken);

            if (hasPayments)
            {
                throw new System.Exception("Cannot cancel appointment. Payment has already been collected.");
            }

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

        // Normalize to uppercase to match backend-set statuses (REPORTED, SCANNED, IN_PROGRESS, etc.)
        appointment.Status = newStatus;
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }
}
