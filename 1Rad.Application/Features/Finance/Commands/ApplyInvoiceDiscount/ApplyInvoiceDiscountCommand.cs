using MediatR;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;

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
            throw new NotFoundException("Bill not found.");

        if (invoice.Status is "PAID" or "CANCELLED")
            throw new BusinessRuleViolationException(
                invoice.Status == "PAID"
                    ? "This bill is already paid. Changes to a paid bill go through Approvals."
                    : "This bill is cancelled and can't be changed.",
                "INVOICE_CLOSED");

        // Self-heal against the live service list BEFORE recomputing totals below —
        // mirrors CollectPaymentCommand's identical block (see there for the full
        // rationale). A draft save recomputes Gross from invoice.Items same as a
        // payment does, so it's exposed to the same staleness if a service was
        // added to the appointment since Items was last synced. Deliberately
        // ADD-ONLY: never remove/alter an existing line here.
        if (invoice.AppointmentId.HasValue)
        {
            var liveServices = await _context.AppointmentServices
                .Where(s => s.AppointmentId == invoice.AppointmentId.Value && s.DeletedAt == null)
                .ToListAsync(cancellationToken);
            var invoicedServiceIds = invoice.Items
                .Where(i => i.AppointmentServiceId.HasValue)
                .Select(i => i.AppointmentServiceId!.Value)
                .ToHashSet();
            foreach (var svc in liveServices.Where(s => !invoicedServiceIds.Contains(s.Id)))
            {
                var newItem = new InvoiceItem
                {
                    InvoiceId = invoice.Id,
                    Description = svc.ServiceName,
                    Amount = svc.Amount,
                    Quantity = 1,
                    AppointmentServiceId = svc.Id,
                };
                invoice.Items.Add(newItem);
                // invoice.Items.Add() alone doesn't reliably track a new item as
                // Added onto an ALREADY-TRACKED invoice (its Id is a non-default
                // Guid set at construction, not by EF) — it can end up Modified,
                // and SaveChanges then issues a no-op UPDATE against a row that
                // was never inserted instead of an INSERT, silently dropping the
                // self-healed line with no error. See UpdateAppointmentCommand's
                // ReconcileInvoiceItemsAsync for the full writeup of this pattern.
                _context.Entry(newItem).State = Microsoft.EntityFrameworkCore.EntityState.Added;
            }
        }

        // Review B3: no negative concessions or charges.
        DiscountPolicy.RejectNegatives(request.CentreDiscount, request.ReferrerDiscount, request.InstitutionalDeduction,
            request.ExtraCharges?.Select(x => x.Amount));
        if (request.DiscountAmount < 0 || request.AdditionalCharges < 0)
            throw new ValidationException("Discounts and charges can't be negative.");

        var originalAdditionalCharges = invoice.AdditionalCharges;
        var oldReferrerDiscount = invoice.ReferrerDiscount;

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

        // Review B7: a draft can't take the bill below what has already been
        // collected — that would hide an overpayment. Lowering a part-paid bill that
        // far is an Edit-payment approval, which refunds the excess properly.
        if (invoice.PaidAmount > invoice.TotalAmount + 0.01m)
            throw new BusinessRuleViolationException(
                $"₹{invoice.PaidAmount:0.##} has already been collected, so the bill can't go below that here. Ask an admin to approve the change; the excess will be refunded.",
                "DISCOUNT_BELOW_COLLECTED");
        PaymentLedger.DeriveStatus(invoice);

        // A referrer discount saved as a draft comes off the referrer's commission
        // now (review R2). Before, collection later saw "no change" and the doctor
        // kept the full cut while the patient got the concession.
        await ReferrerDiscountCommission.ApplyAsync(
            _context, invoice, oldReferrerDiscount, absorbExcessToCentre: false,
            $"draft saved by user {_context.UserContext.UserId}", cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
