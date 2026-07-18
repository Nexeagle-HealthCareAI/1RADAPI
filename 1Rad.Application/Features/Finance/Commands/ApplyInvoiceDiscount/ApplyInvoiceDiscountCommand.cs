using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

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

        // Recalculate Gross if needed, though it should be stable
        var grossAmount = invoice.Items.Sum(x => x.Amount * x.Quantity);
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
            
            if (request.ExtraCharges != null && request.ExtraCharges.Any())
            {
                _context.InvoiceExtraCharges.RemoveRange(invoice.ExtraCharges);
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
                        invoice.ExtraCharges.Add(newCharge);
                    }
                }
                
                invoice.AdditionalCharges = invoice.ExtraCharges.Sum(x => x.Amount);
                invoice.AdditionalChargesReason = string.Join(" | ", invoice.ExtraCharges.Select(x => $"{x.Reason}: {x.Amount}"));
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
