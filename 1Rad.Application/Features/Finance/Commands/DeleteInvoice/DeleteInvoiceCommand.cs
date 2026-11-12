using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Finance.Commands.DeleteInvoice;

public record DeleteInvoiceCommand(Guid InvoiceId, Guid? CommissionId = null) : IRequest<(bool Success, string? Error)>;

public class DeleteInvoiceCommandHandler : IRequestHandler<DeleteInvoiceCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public DeleteInvoiceCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(bool Success, string? Error)> Handle(DeleteInvoiceCommand request, CancellationToken cancellationToken)
    {
        var invoice = await _context.Invoices
            .Include(i => i.Items)
            .Include(i => i.Payments)
            .Include(i => i.Appointment)
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.HospitalId == _context.UserContext.HospitalId, cancellationToken);

        if (invoice == null)
        {
            return (false, "Invoice not found or unauthorized.");
        }

        if (invoice.Status != "PAID" && invoice.Appointment != null)
        {
            var appStatus = invoice.Appointment.Status?.ToLower();
            if (appStatus == "scanned" || appStatus == "reporting" || appStatus == "reported" || appStatus == "completed")
            {
                return (false, "This invoice cannot be deleted because the associated study has already been scanned or processed.");
            }
        }

        // B2: money already collected on this bill must be refunded through the payment
        // screen first; deleting the bill used to drop the payment rows silently.
        var collected = (invoice.Payments?.Sum(p => p.Amount) ?? 0m);
        if (collected > 0.005m || invoice.PaidAmount > 0.005m)
        {
            return (false, "This bill has payments recorded against it. Refund or remove the payments first, then delete the bill.");
        }

        var invoiceStr = invoice.InvoiceId;
        var invoiceGuidStr = invoice.Id.ToString();
        var appointmentId = invoice.AppointmentId;
        var hospitalId = _context.UserContext.HospitalId;

        // B2: every commission tied to this bill or visit, not just the first match.
        var commissions = await _context.ReferralCommissions
            .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null &&
                        ((request.CommissionId != null && c.Id == request.CommissionId) ||
                         c.ReferenceNumber == invoiceStr ||
                         c.ReferenceNumber == invoiceGuidStr ||
                         (appointmentId != null && c.AppointmentId == appointmentId)))
            .ToListAsync(cancellationToken);

        if (commissions.Any(c => string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase)))
        {
            return (false, "A referral commission on this visit has already been paid out. Reverse that payout before deleting the bill.");
        }

        var deleteNow = DateTime.UtcNow;
        var referrersToRecalculate = new HashSet<Guid>();
        foreach (var commission in commissions)
        {
            referrersToRecalculate.Add(commission.ReferrerId);
            commission.DeletedAt = deleteNow;
            commission.UpdatedAt = deleteNow;
            commission.Remarks = string.IsNullOrEmpty(commission.Remarks)
                ? $"[Bill {invoiceStr} deleted]"
                : $"{commission.Remarks} [Bill {invoiceStr} deleted]";
        }

        invoice.Status = "CANCELLED";
        invoice.DeletedAt = deleteNow;
        invoice.UpdatedAt = deleteNow;
        await _context.SaveChangesAsync(cancellationToken);

        // Cascade recalculation of Accumulated Totals for the referrer to prevent ledger drift after deletion
        if (referrersToRecalculate.Count > 0)
        {
            foreach (var referrerId in referrersToRecalculate)
                await ReferralLedger.RecomputeAccumulatedTotal(_context, referrerId, hospitalId, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return (true, null);
    }
}
