using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.CollectPayment;

public record CollectPaymentCommand : IRequest<bool>
{
    public Guid InvoiceId { get; init; }
    public decimal Amount { get; init; }
    public string PaymentMethod { get; init; } = "CASH";
    public string? Reference { get; init; }
}

public class CollectPaymentCommandHandler : IRequestHandler<CollectPaymentCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public CollectPaymentCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(CollectPaymentCommand request, CancellationToken cancellationToken)
    {
        var invoice = await _context.Invoices
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.HospitalId == _context.UserContext.HospitalId, cancellationToken);
        
        if (invoice == null) throw new Exception("Invoice not found.");
        if (invoice.Status == "PAID") throw new Exception("Invoice is already settled.");

        var payment = new Payment
        {
            InvoiceId = request.InvoiceId,
            Amount = request.Amount,
            PaymentMethod = request.PaymentMethod,
            TransactionReference = request.Reference,
            CreatedAt = DateTime.UtcNow
        };

        _context.Payments.Add(payment);

        invoice.PaidAmount += request.Amount;
        
        if (invoice.PaidAmount >= invoice.TotalAmount)
        {
            invoice.Status = "PAID";
            invoice.PaidAt = DateTime.UtcNow;
        }
        else
        {
            invoice.Status = "PARTIAL";
        }

        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }
}
