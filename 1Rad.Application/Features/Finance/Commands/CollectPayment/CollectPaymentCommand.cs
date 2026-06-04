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

    // Set when the referral concession exceeds the doctor's commission: the
    // excess becomes his carried deficit (a negative commission). Audited below.
    public decimal? CommissionDeficit { get; init; }
    public string? DeficitReason { get; init; }

    // Alternative to the deficit: when true, the excess above the doctor's
    // commission is funded by the CENTRE instead. The commission is floored at
    // zero and the excess is moved into the centre discount so the centre
    // absorbs it (no negative commission, nothing recovered from the referrer).
    public bool AbsorbExcessToCentre { get; init; }
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

                // Eligible commission before any referral concession this cycle.
                var baseCommission = (commission?.CommissionAmount ?? 0) + oldReferrerDiscount;

                // Over-commission funded by the CENTRE: floor the commission at zero
                // and shift the excess into the centre discount so the centre — not
                // the referrer — absorbs it. The patient's total is unchanged (the
                // excess only moves between the two discount buckets), so just the
                // split + commission change.
                if (request.AbsorbExcessToCentre && invoice.ReferrerDiscount > baseCommission)
                {
                    var excess = invoice.ReferrerDiscount - baseCommission;
                    invoice.CentreDiscount += excess;
                    invoice.ReferrerDiscount = baseCommission;
                    invoice.DiscountAmount = invoice.CentreDiscount + invoice.ReferrerDiscount + invoice.InstitutionalDeduction;
                    invoice.TotalAmount = gross - invoice.DiscountAmount;
                    if (commission != null)
                        commission.Remarks = (commission.Remarks ?? "") + $" [Excess ₹{excess:0.##} absorbed by centre]";
                }

                if (commission != null)
                {
                    commission.CommissionAmount += oldReferrerDiscount; // Revert
                    commission.CommissionAmount -= invoice.ReferrerDiscount; // Apply New
                    commission.Remarks = (commission.Remarks ?? "") + $" [Adj: ₹{oldReferrerDiscount} -> ₹{invoice.ReferrerDiscount}]";

                    // Over-commission concession → the commission is now negative; the
                    // doctor carries that deficit, recovered from future referrals. Audit
                    // the authoriser (the authenticated user) + reason so the credit
                    // decision is traceable. The amount itself is intentionally NOT
                    // clamped to zero — the negative is the whole point.
                    if (commission.CommissionAmount < 0)
                    {
                        var deficit = Math.Abs(commission.CommissionAmount);
                        var reason = string.IsNullOrWhiteSpace(request.DeficitReason) ? "" : $" — {request.DeficitReason.Trim()}";
                        commission.Remarks += $" [DEFICIT ₹{deficit:0.##} authorised by user {_context.UserContext.UserId}{reason}]";
                    }
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

