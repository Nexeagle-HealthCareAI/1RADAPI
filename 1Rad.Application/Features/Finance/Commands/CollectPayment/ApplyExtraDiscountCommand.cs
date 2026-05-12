using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Finance.Commands.CollectPayment;

public record ApplyExtraDiscountCommand : IRequest<bool>
{
    public Guid InvoiceId { get; init; }
    public decimal ExtraDiscount { get; init; }
}

public class ApplyExtraDiscountCommandHandler : IRequestHandler<ApplyExtraDiscountCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public ApplyExtraDiscountCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(ApplyExtraDiscountCommand request, CancellationToken cancellationToken)
    {
        var invoice = await _context.Invoices
            .Include(i => i.Payments)
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.HospitalId == _context.UserContext.HospitalId, cancellationToken);

        if (invoice == null)
        {
            throw new KeyNotFoundException($"Invoice with ID '{request.InvoiceId}' not found.");
        }

        // Apply additional discount to existing discount
        invoice.DiscountAmount += request.ExtraDiscount;
        
        // Recalculate total
        var gross = invoice.GrossAmount > 0 ? invoice.GrossAmount : invoice.Items.Sum(x => x.Amount * x.Quantity);
        invoice.GrossAmount = gross;
        invoice.TotalAmount = gross - invoice.DiscountAmount;

        // Since it's an adjustment, we might need to record a 'REBATE' payment or just let the balance go negative/positive
        // To keep ledger clean, we'll ensure status reflects the new total
        if (invoice.PaidAmount >= invoice.TotalAmount)
        {
            invoice.Status = "PAID";
        }
        else
        {
            invoice.Status = "PARTIAL";
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
