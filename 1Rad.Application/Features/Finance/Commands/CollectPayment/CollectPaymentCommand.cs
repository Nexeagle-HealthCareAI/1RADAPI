using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.CollectPayment;

public record CollectPaymentCommand : IRequest<bool>
{
    public Guid InvoiceId { get; init; }
    public decimal Amount { get; init; }
    public decimal? CentreDiscount { get; init; }
    public decimal? ReferrerDiscount { get; init; }
    public decimal? Deduction { get; init; }
    public string PaymentMethod { get; init; } = "CASH";
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
        try
        {
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                throw new UnauthorizedAccessException("Hospital context is required to collect payment.");
            }

            var invoice = await _context.Invoices
                .Include(i => i.Payments)
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.HospitalId == _context.UserContext.HospitalId, cancellationToken);
            
            if (invoice == null)
            {
                throw new KeyNotFoundException($"Invoice with ID '{request.InvoiceId}' not found.");
            }

            if (invoice.Status == "CANCELLED")
            {
                throw new InvalidOperationException($"Invoice '{invoice.InvoiceId}' is cancelled.");
            }

            // Record Previous State for Commission Differential
            var oldReferrerDiscount = invoice.ReferrerDiscount;

            // Update Deduction Vectors (Overwrite with request values if provided, else keep existing)
            invoice.CentreDiscount = request.CentreDiscount ?? invoice.CentreDiscount;
            invoice.ReferrerDiscount = request.ReferrerDiscount ?? invoice.ReferrerDiscount;
            invoice.InstitutionalDeduction = request.Deduction ?? invoice.InstitutionalDeduction;

            var totalDiscount = invoice.CentreDiscount + invoice.ReferrerDiscount + invoice.InstitutionalDeduction;
            
            // Re-anchor Gross to prevent drift
            var gross = invoice.Items.Any() 
                ? invoice.Items.Sum(x => x.Amount * x.Quantity)
                : (invoice.GrossAmount > 0 ? invoice.GrossAmount : invoice.TotalAmount + invoice.DiscountAmount);
            
            invoice.GrossAmount = gross;
            invoice.DiscountAmount = totalDiscount;
            invoice.TotalAmount = gross - totalDiscount;

            // Handle Referrer-side adjustment (Differential logic)
            if (invoice.ReferrerDiscount != oldReferrerDiscount)
            {
                var commission = await _context.ReferralCommissions
                    .FirstOrDefaultAsync(c => 
                        (c.AppointmentId == invoice.AppointmentId || (c.ReferenceNumber == invoice.InvoiceId && c.ReferenceNumber != null)) && 
                        c.HospitalId == _context.UserContext.HospitalId, cancellationToken);
                
                if (commission != null)
                {
                    commission.CommissionAmount += oldReferrerDiscount; // Revert
                    commission.CommissionAmount -= invoice.ReferrerDiscount; // Apply New
                    commission.Remarks = (commission.Remarks ?? "") + $" [Adj: ₹{oldReferrerDiscount} -> ₹{invoice.ReferrerDiscount}]";
                }
            }

            // Process Optional Payment
            if (request.Amount > 0)
            {
                var remainingBalance = invoice.TotalAmount - invoice.PaidAmount;
                if (request.Amount > remainingBalance + 0.01m)
                {
                    throw new InvalidOperationException($"Payment (₹{request.Amount}) exceeds balance (₹{remainingBalance}).");
                }

                var payment = new Payment
                {
                    InvoiceId = request.InvoiceId,
                    Amount = request.Amount,
                    PaymentMethod = request.PaymentMethod,
                    CreatedAt = DateTime.UtcNow,
                    HospitalId = _context.UserContext.HospitalId
                };

                _context.Payments.Add(payment);
                invoice.PaidAmount += request.Amount;
            }

            // Status Management
            if (invoice.PaidAmount >= invoice.TotalAmount - 0.01m)
            {
                invoice.Status = "PAID";
                invoice.PaidAt = DateTime.UtcNow;
            }
            else if (invoice.PaidAmount > 0)
            {
                invoice.Status = "PARTIAL";
            }

            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception)
        {
            throw;
        }
    }
}

