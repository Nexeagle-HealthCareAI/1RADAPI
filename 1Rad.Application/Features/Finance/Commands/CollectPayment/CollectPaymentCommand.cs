using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.CollectPayment;

public struct ExtraChargeDetail
{
    public string Reason { get; init; }
    public decimal Amount { get; init; }
}

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

    public decimal? AdditionalCharges { get; init; }
    public string? AdditionalChargesReason { get; init; }

    public List<ExtraChargeDetail>? ExtraCharges { get; init; }
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

            if (request.Amount <= 0)
            {
                throw new ArgumentException("Payment amount must be greater than zero.", nameof(request.Amount));
            }

            var invoice = await _context.Invoices
                .Include(i => i.Payments)
                .Include(i => i.Items)
                .Include(i => i.ExtraCharges)
                .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.HospitalId == _context.UserContext.HospitalId, cancellationToken);
            
            if (invoice == null)
            {
                throw new KeyNotFoundException($"Invoice with ID '{request.InvoiceId}' not found.");
            }

            if (invoice.Status == "CANCELLED")
            {
                throw new InvalidOperationException($"Invoice '{invoice.InvoiceId}' is cancelled.");
            }

            if (invoice.PaidAmount >= invoice.TotalAmount - 0.01m)
            {
                throw new InvalidOperationException($"Invoice '{invoice.InvoiceId}' is already settled.");
            }

            // Record Previous State for Commission Differential
            var oldReferrerDiscount = invoice.ReferrerDiscount;
            // Capture original AdditionalCharges BEFORE we mutate it in the
            // ExtraCharges block below — needed for the correct gross fallback.
            var originalAdditionalCharges = invoice.AdditionalCharges;

            // Update Deduction Vectors (Overwrite with request values if provided, else keep existing)
            invoice.CentreDiscount = request.CentreDiscount ?? invoice.CentreDiscount;
            invoice.ReferrerDiscount = request.ReferrerDiscount ?? invoice.ReferrerDiscount;
            invoice.InstitutionalDeduction = request.Deduction ?? invoice.InstitutionalDeduction;
            
            // Process new ExtraCharges list if provided, otherwise fallback to legacy scalars
            // A non-null list is authoritative — including an EMPTY list, which
            // means every extra charge was intentionally removed. Gating this on
            // .Any() left old InvoiceExtraCharge rows orphaned in the DB while the
            // reason field got blanked, so the drawer showed no charges even though
            // stale rows still existed out of sync.
            if (request.ExtraCharges != null)
            {
                // Re-query by InvoiceId to avoid EF tracking mismatches that can
                // leave stale rows when the navigation collection was populated by a
                // prior draft-save cycle (the tracked state may differ from what's
                // actually in the DB, causing RemoveRange to silently skip records).
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
                
                // Aggregate into the main fields for fast reading/backward compatibility
                invoice.AdditionalCharges = invoice.ExtraCharges.Sum(x => x.Amount);
                invoice.AdditionalChargesReason = request.AdditionalChargesReason ?? "[]";
            }
            else
            {
                invoice.AdditionalCharges = request.AdditionalCharges ?? invoice.AdditionalCharges;
                invoice.AdditionalChargesReason = request.AdditionalChargesReason ?? invoice.AdditionalChargesReason;
            }

            var totalDiscount = invoice.CentreDiscount + invoice.ReferrerDiscount + invoice.InstitutionalDeduction;
            
            // Re-anchor Gross to prevent drift.
            // IMPORTANT: use originalAdditionalCharges (captured before mutation)
            // in the fallback formula, not invoice.AdditionalCharges which was
            // already updated above. This prevents extra charges from vanishing or
            // inflating when there are no Items rows to sum directly.
            var gross = invoice.Items.Any() 
                ? invoice.Items.Sum(x => x.Amount * x.Quantity)
                : (invoice.GrossAmount > 0 ? invoice.GrossAmount : invoice.TotalAmount + invoice.DiscountAmount - originalAdditionalCharges);
            
            invoice.GrossAmount = gross;
            invoice.DiscountAmount = totalDiscount;
            invoice.TotalAmount = gross + invoice.AdditionalCharges - totalDiscount;

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

            // Process Optional Payment. Overpayment is NO LONGER rejected — the
            // part that fits the balance settles the invoice; any EXCESS is parked
            // as a patient credit/advance (later refundable or carried forward).
            // The accountant just enters the cash tendered; the split is automatic.
            if (request.Amount > 0)
            {
                var remainingBalance = invoice.TotalAmount - invoice.PaidAmount;
                var applied = Math.Min(request.Amount, Math.Max(0m, remainingBalance));
                var excess = request.Amount - applied;

                if (applied > 0)
                {
                    _context.Payments.Add(new Payment
                    {
                        InvoiceId = request.InvoiceId,
                        Amount = applied,
                        PaymentMethod = request.PaymentMethod,
                        CreatedAt = DateTime.UtcNow,
                        HospitalId = _context.UserContext.HospitalId
                    });
                    invoice.PaidAmount += applied;
                }

                if (excess > 0.009m)
                {
                    _context.CreditTransactions.Add(new CreditTransaction
                    {
                        HospitalId = _context.UserContext.HospitalId,
                        PatientId = invoice.PatientId,
                        PatientName = invoice.PatientName ?? string.Empty,
                        Type = "ADVANCE",
                        Amount = Math.Round(excess, 2),
                        InvoiceId = invoice.Id,
                        InvoiceDisplayId = invoice.InvoiceId,
                        PaymentMethod = request.PaymentMethod,
                        CreatedByUserId = _context.UserContext.UserId,
                        Remarks = "Advance / overpayment held at collection",
                        CreatedAt = DateTime.UtcNow,
                    });
                }
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

            // Free → paid: once the patient actually pays, the "free test" flag
            // must drop off — the invoice is no longer free. Only WHOLE-invoice
            // frees carry invoice.IsFree=true (a mixed per-service free leaves it
            // false), so this never disturbs a visit where only some lines are free.
            if (invoice.IsFree && invoice.PaidAmount > 0)
            {
                invoice.IsFree = false;
                foreach (var it in invoice.Items) it.IsFree = false;
                if (invoice.AppointmentId.HasValue)
                {
                    var freedServices = await _context.AppointmentServices
                        .Where(s => s.AppointmentId == invoice.AppointmentId.Value && s.HospitalId == invoice.HospitalId)
                        .ToListAsync(cancellationToken);
                    foreach (var s in freedServices) { s.IsFree = false; s.UpdatedAt = DateTime.UtcNow; }
                }
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

