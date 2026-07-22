using MediatR;
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

        // Recalculate Gross if needed, though it should be stable.
        // If Items is empty (e.g. pre-arrival), preserve the existing GrossAmount which was based on AppointmentServices.
        var grossAmount = invoice.Items.Any() ? invoice.Items.Sum(x => x.Amount * x.Quantity) : invoice.GrossAmount;
        invoice.GrossAmount = grossAmount;
        
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
                // A non-null list is authoritative — including an EMPTY list, which
                // means the caller (e.g. the settlement drawer's "Save as draft")
                // intentionally removed every extra charge. Previously an empty list
                // fell through to the scalar branch below, which left the old
                // InvoiceExtraCharge rows orphaned in the DB while blanking the
                // AdditionalChargesReason field the drawer reads from — making the
                // charges appear to vanish while stale rows lingered out of sync.
                // Re-query by InvoiceId to avoid EF tracking mismatches (same
                // fix as CollectPaymentCommand — navigation collection state can
                // diverge from what's in the DB after a prior draft cycle).
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
                
                invoice.AdditionalCharges = invoice.ExtraCharges.Sum(x => x.Amount);
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
        if (discount > grossAmount + invoice.AdditionalCharges)
        {
            discount = grossAmount + invoice.AdditionalCharges;
        }

        invoice.DiscountAmount = discount;
        invoice.TotalAmount = grossAmount + invoice.AdditionalCharges - discount;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
