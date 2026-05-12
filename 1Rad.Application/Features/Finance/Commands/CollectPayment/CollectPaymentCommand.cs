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
            // Validate hospital context
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                throw new UnauthorizedAccessException("Hospital context is required to collect payment.");
            }

            // Validate payment amount
            if (request.Amount <= 0)
            {
                throw new ArgumentException("Payment amount must be greater than zero.", nameof(request.Amount));
            }

            var invoice = await _context.Invoices
                .Include(i => i.Payments)
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.HospitalId == _context.UserContext.HospitalId, cancellationToken);

            
            if (invoice == null)
            {
                throw new KeyNotFoundException($"Invoice with ID '{request.InvoiceId}' not found or does not belong to your hospital.");
            }

            if (invoice.Status == "PAID")
            {
                throw new InvalidOperationException($"Invoice '{invoice.InvoiceId}' is already fully paid. No additional payment can be collected.");
            }

            if (invoice.Status == "CANCELLED")
            {
                throw new InvalidOperationException($"Invoice '{invoice.InvoiceId}' is cancelled. Payment cannot be collected.");
            }

            // Apply Three-Tier Discounts/Deductions
            var totalDiscount = (request.CentreDiscount ?? 0) + (request.ReferrerDiscount ?? 0) + (request.Deduction ?? 0);
            if (totalDiscount > 0)
            {
                var gross = invoice.Items.Any() 
                    ? invoice.Items.Sum(x => x.Amount * x.Quantity)
                    : (invoice.GrossAmount > 0 ? invoice.GrossAmount : invoice.TotalAmount);
                
                invoice.GrossAmount = gross;
                invoice.DiscountAmount = totalDiscount;
                invoice.TotalAmount = gross - totalDiscount;

                // Handle Referrer-side adjustment if requested
                if ((request.ReferrerDiscount ?? 0) > 0 && invoice.AppointmentId.HasValue)
                {
                    var commission = await _context.ReferralCommissions
                        .FirstOrDefaultAsync(c => c.AppointmentId == invoice.AppointmentId && c.HospitalId == _context.UserContext.HospitalId, cancellationToken);
                    
                    if (commission != null)
                    {
                        commission.CommissionAmount -= request.ReferrerDiscount.Value;
                        commission.Remarks = (commission.Remarks ?? "") + $" [Burden-Share: ₹{request.ReferrerDiscount.Value} deducted]";
                    }
                }
            }






            // Check if payment exceeds remaining balance
            var remainingBalance = invoice.TotalAmount - invoice.PaidAmount;
            if (request.Amount > remainingBalance + 0.01m) // Allow small epsilon for floating point
            {
                throw new InvalidOperationException($"Payment amount (₹{request.Amount:N2}) exceeds remaining balance (₹{remainingBalance:N2}).");
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
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to collect payment: {ex.Message}", ex);
        }
    }
}
