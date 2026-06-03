using MediatR;
using Microsoft.EntityFrameworkCore;
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

        var invoiceGuidStr = invoice.Id.ToString();
        var invoiceStr = invoice.InvoiceId;
        var appointmentId = invoice.AppointmentId;
        var hospitalId = _context.UserContext.HospitalId;

        ReferralCommission? commission = null;
        if (request.CommissionId.HasValue)
        {
            commission = await _context.ReferralCommissions
                .FirstOrDefaultAsync(c => c.Id == request.CommissionId.Value && c.HospitalId == hospitalId, cancellationToken);
        }

        if (commission == null)
        {
            // Enhanced multi-strategy lookup for related referral commissions
            commission = await _context.ReferralCommissions
                .FirstOrDefaultAsync(c => 
                    ((c.ReferenceNumber == invoiceStr) || 
                     (c.ReferenceNumber == invoiceGuidStr) ||
                     (appointmentId != null && c.AppointmentId == appointmentId)) &&
                    c.HospitalId == hospitalId, cancellationToken);
        }
        
        var deleteNow = DateTime.UtcNow;

        Guid? referrerIdToRecalculate = null;
        if (commission != null)
        {
            referrerIdToRecalculate = commission.ReferrerId;
            // SOFT-delete so the Referral Hub's offline cache tombstones it.
            commission.DeletedAt = deleteNow;
            commission.UpdatedAt = deleteNow;
            commission.CommissionAmount = 0;
        }

        // Payments are a real financial event — clear them so the invoice's
        // collected total resets, but do it before tombstoning the header.
        if (invoice.Payments != null && invoice.Payments.Any())
        {
            _context.Payments.RemoveRange(invoice.Payments);
            invoice.PaidAmount = 0;
        }

        // SOFT-delete the invoice header (NOT a hard delete) — a hard delete
        // never reaches the offline cache as a tombstone, so the billing page
        // would keep showing it. DeletedAt + UpdatedAt makes the sync remove it.
        invoice.DeletedAt = deleteNow;
        invoice.UpdatedAt = deleteNow;
        await _context.SaveChangesAsync(cancellationToken);

        // Cascade recalculation of Accumulated Totals for the referrer to prevent ledger drift after deletion
        if (referrerIdToRecalculate.HasValue)
        {
            var allCommissions = await _context.ReferralCommissions
                .Where(c => c.ReferrerId == referrerIdToRecalculate.Value && c.HospitalId == hospitalId && c.DeletedAt == null)
                .OrderBy(c => c.TransactionDate)
                .ToListAsync(cancellationToken);

            decimal runningTotal = 0;
            foreach (var c in allCommissions)
            {
                runningTotal += c.CommissionAmount;
                c.AccumulatedTotal = runningTotal;
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        return (true, null);
    }
}
