using MediatR;
using _1Rad.Application.Common;
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

        // A cancelled invoice is closed/refunded audit history — unlike PAID, it
        // must never accept an adjustment. (This command's whole purpose is
        // adjusting bills that ARE already paid — "post-payment adjustment",
        // see useInvoiceActions.handleApplyAdjustment — so PAID is deliberately
        // NOT blocked here, unlike ApplyInvoiceDiscountCommand's pre-payment
        // discount editor.)
        if (invoice.Status == "CANCELLED")
        {
            throw new InvalidOperationException($"Invoice '{invoice.InvoiceId}' is cancelled.");
        }

        // Apply additional discount to existing discount. RecomputeGross must run
        // BEFORE this new discount is finalized (see InvoiceTotals) — but this
        // command derives its new discount by incrementing the OLD value, so grab
        // it first, recompute gross, then finalize with the incremented figure.
        var newDiscount = invoice.DiscountAmount + request.ExtraDiscount;
        InvoiceTotals.RecomputeGross(invoice, invoice.AdditionalCharges);
        InvoiceTotals.ApplyDiscountAndFinalize(invoice, newDiscount);

        // A discount applied AFTER payment was collected can push PaidAmount
        // above the new, lower TotalAmount — the patient is now owed the
        // difference. Every other overpay path in this codebase (CollectPayment,
        // UpdateAppointment's service-removal gap) books that excess as a
        // credit instead of letting it vanish into a misleading "PAID" status;
        // this command was the one place that didn't.
        var overpay = invoice.PaidAmount - invoice.TotalAmount;
        if (overpay > 0.009m)
        {
            _context.CreditTransactions.Add(new CreditTransaction
            {
                HospitalId = invoice.HospitalId,
                PatientId = invoice.PatientId,
                PatientName = invoice.PatientName ?? string.Empty,
                Type = "ADVANCE",
                Amount = Math.Round(overpay, 2),
                InvoiceId = invoice.Id,
                InvoiceDisplayId = invoice.InvoiceId,
                CreatedByUserId = _context.UserContext.UserId,
                Remarks = "Advance held — extra discount applied after payment",
                CreatedAt = DateTime.UtcNow,
            });
            invoice.PaidAmount = invoice.TotalAmount;
        }

        // Ensure status reflects the new total.
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
