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

        var commission = await _context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.ReferenceNumber == invoice.InvoiceId && c.HospitalId == _context.UserContext.HospitalId, cancellationToken);
        
        if (commission != null)
        {
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

        return true;
    }
}
