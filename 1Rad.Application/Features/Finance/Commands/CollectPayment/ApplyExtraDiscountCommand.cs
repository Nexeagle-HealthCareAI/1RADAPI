using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.ApplyExtraDiscount;

public record ApplyExtraDiscountCommand(Guid InvoiceId, decimal ExtraDiscount) : IRequest<bool>;

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
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.HospitalId == _context.UserContext.HospitalId, cancellationToken);

        if (invoice == null) return false;

        // Apply adjustment: Increase discount and reduce net/paid to reflect the refund/concession
        invoice.DiscountAmount += request.ExtraDiscount;
        invoice.TotalAmount -= request.ExtraDiscount;
        invoice.PaidAmount -= request.ExtraDiscount;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
