using MediatR;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Finance.Commands.ApplyInvoiceDiscount;

public struct ExtraChargeDetail
{
    public string Reason { get; init; }
    public decimal Amount { get; init; }
}

public record ApplyInvoiceDiscountCommand : IRequest<bool>
{
    public Guid InvoiceId { get; init; }
    public decimal DiscountAmount { get; init; }
    // Optional discount breakdown — sent by the settlement drawer's "Save as
    // draft" so reopening the invoice restores the partial edits. When any are
    // supplied, the total discount is derived from them.
    public decimal? CentreDiscount { get; init; }
    public decimal? ReferrerDiscount { get; init; }
    public decimal? InstitutionalDeduction { get; init; }
    public decimal? AdditionalCharges { get; init; }
    public string? AdditionalChargesReason { get; init; }
    public List<ExtraChargeDetail>? ExtraCharges { get; init; }
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
            .Include(i => i.ExtraCharges)
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId, cancellationToken);

        if (invoice == null)
        {
            throw new KeyNotFoundException($"Invoice with ID '{request.InvoiceId}' not found.");
        }

        if (invoice.Status == "PAID")
        {
            throw new InvalidOperationException("Cannot apply discount to an already paid invoice.");
        }

        var originalAdditionalCharges = invoice.AdditionalCharges;

        // When the settlement drawer saves a DRAFT it sends the discount
        // breakdown; persist it and derive the total from it so reopening the
        // invoice restores the partial edits (centre / referrer / deduction).
        var hasBreakdown = request.CentreDiscount.HasValue
                         || request.ReferrerDiscount.HasValue
                         || request.InstitutionalDeduction.HasValue
                         || request.AdditionalCharges.HasValue;
        if (hasBreakdown)
        {
            invoice.CentreDiscount         = request.CentreDiscount         ?? invoice.CentreDiscount;
            invoice.ReferrerDiscount       = request.ReferrerDiscount       ?? invoice.ReferrerDiscount;
            invoice.InstitutionalDeduction = request.InstitutionalDeduction ?? invoice.InstitutionalDeduction;
            
            if (request.ExtraCharges != null)
            {
                var existingCharges = await _context.InvoiceExtraCharges
                    .Where(ec => ec.InvoiceId == invoice.Id)
                    .ToListAsync(cancellationToken);
                if (existingCharges.Count > 0)
                    _context.InvoiceExtraCharges.RemoveRange(existingCharges);
                invoice.ExtraCharges.Clear();
                
                foreach (var ec in request.ExtraCharges)
                {
                    if (ec.Amount > 0)
                    {
                        var newCharge = new InvoiceExtraCharge
                        {
                            InvoiceId = invoice.Id,
                            Reason = string.IsNullOrWhiteSpace(ec.Reason) ? "Extra Charge" : ec.Reason.Trim(),
                            Amount = ec.Amount,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.InvoiceExtraCharges.Add(newCharge);
                    }
                }

                // Sum the validated INPUT, not invoice.ExtraCharges — EF's relationship
                // fixup (triggered by the _context.InvoiceExtraCharges.Add above) already
                // attaches each newCharge into invoice.ExtraCharges on its own, so also
                // calling invoice.ExtraCharges.Add(newCharge) here left every charge
                // counted twice in that in-memory collection (same object reference added
                // twice) — the DB got one row each, but Sum() over the doubled collection
                // silently doubled AdditionalCharges/GrossAmount/TotalAmount. Deriving the
                // total straight from request.ExtraCharges (the same filter used above)
                // is unambiguous and independent of EF's fixup timing.
                invoice.AdditionalCharges = request.ExtraCharges.Where(x => x.Amount > 0).Sum(x => x.Amount);
                invoice.AdditionalChargesReason = request.AdditionalChargesReason ?? "[]";
            }
            else
            {
                invoice.AdditionalCharges      = request.AdditionalCharges      ?? invoice.AdditionalCharges;
                invoice.AdditionalChargesReason= request.AdditionalChargesReason?? invoice.AdditionalChargesReason;
            }
        }

        var discount = hasBreakdown
            ? invoice.CentreDiscount + invoice.ReferrerDiscount + invoice.InstitutionalDeduction
            : request.DiscountAmount;

        // Canonical recompute (Common/InvoiceTotals.cs).
        InvoiceTotals.RecomputeGross(invoice, originalAdditionalCharges);
        InvoiceTotals.ApplyDiscountAndFinalize(invoice, discount);

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
