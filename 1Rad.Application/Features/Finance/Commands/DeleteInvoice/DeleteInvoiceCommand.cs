using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Finance.Commands.DeleteInvoice;

public record DeleteInvoiceCommand(Guid InvoiceId) : IRequest<bool>;

public class DeleteInvoiceCommandHandler : IRequestHandler<DeleteInvoiceCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public DeleteInvoiceCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(DeleteInvoiceCommand request, CancellationToken cancellationToken)
    {
        var invoice = await _context.Invoices
            .Include(i => i.Items)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.HospitalId == _context.UserContext.HospitalId, cancellationToken);

        if (invoice == null)
        {
            throw new Exception("Invoice not found or unauthorized.");
        }

        var invoiceGuidStr = invoice.Id.ToString();
        var invoiceStr = invoice.InvoiceId;
        var appointmentId = invoice.AppointmentId;
        var hospitalId = _context.UserContext.HospitalId;

        // Enhanced multi-strategy lookup for related referral commissions
        var commission = await _context.ReferralCommissions
            .FirstOrDefaultAsync(c => 
                ((c.ReferenceNumber == invoiceStr) || 
                 (c.ReferenceNumber == invoiceGuidStr) ||
                 (appointmentId != null && c.AppointmentId == appointmentId)) &&
                c.HospitalId == hospitalId, cancellationToken);
        
        Guid? referrerIdToRecalculate = null;
        if (commission != null)
        {
            referrerIdToRecalculate = commission.ReferrerId;
            _context.ReferralCommissions.Remove(commission);
        }

        if (invoice.Payments != null && invoice.Payments.Any())
        {
            _context.Payments.RemoveRange(invoice.Payments);
        }

        if (invoice.Items != null && invoice.Items.Any())
        {
            ((DbContext)_context).RemoveRange(invoice.Items);
        }

        _context.Invoices.Remove(invoice);
        await _context.SaveChangesAsync(cancellationToken);

        // Cascade recalculation of Accumulated Totals for the referrer to prevent ledger drift after deletion
        if (referrerIdToRecalculate.HasValue)
        {
            var allCommissions = await _context.ReferralCommissions
                .Where(c => c.ReferrerId == referrerIdToRecalculate.Value && c.HospitalId == hospitalId)
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

        return true;
    }
}
