using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.ApplyInvoiceDiscount;

public record ApplyInvoiceDiscountCommand : IRequest<bool>
{
    public Guid InvoiceId { get; init; }
    public decimal DiscountAmount { get; init; }
}

public class ApplyInvoiceDiscountCommandHandler : IRequestHandler<ApplyInvoiceDiscountCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public ApplyInvoiceDiscountCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(ApplyInvoiceDiscountCommand request, CancellationToken cancellationToken)
    {
        var invoice = await _context.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId, cancellationToken);

        if (invoice == null)
        {
            throw new KeyNotFoundException($"Invoice with ID '{request.InvoiceId}' not found.");
        }

        if (invoice.Status == "PAID")
        {
            throw new InvalidOperationException("Cannot apply discount to an already paid invoice.");
        }

        // Recalculate Gross if needed, though it should be stable
        var grossAmount = invoice.Items.Sum(x => x.Amount * x.Quantity);
        invoice.GrossAmount = grossAmount;
        
        var discount = request.DiscountAmount;
        if (discount > grossAmount)
        {
            discount = grossAmount;
        }

        invoice.DiscountAmount = discount;
        invoice.TotalAmount = grossAmount - discount;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
