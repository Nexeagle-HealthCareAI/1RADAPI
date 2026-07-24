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

            if (request.Amount < 0)
            {
                throw new ArgumentException("Payment amount cannot be negative.", nameof(request.Amount));
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

            // Self-heal against the live service list BEFORE recomputing totals below.
            // Recomputing Gross/TotalAmount from invoice.Items (further down) only helps
            // if Items itself is complete — but the thing responsible for keeping Items in
            // sync with AppointmentServices is a completely separate code path (an
            // appointment edit's reconciler, arrival billing, etc.), and if THAT path ever
            // fails to run, skips a line, or predates a fix to it, this invoice is left
            // permanently missing a paid-for service with no way to notice. Payment
            // collection is the last gate before money changes hands, so it must never
            // just trust whatever Items happen to be persisted — pull in any live,
            // un-invoiced service itself. Mirrors ReconcileInvoiceItemsAsync's "add
            // missing" branch; deliberately ADD-ONLY (never remove/alter an existing
            // line here — that's the edit flow's decision, not payment's).
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
                    invoice.Items.Add(new InvoiceItem
                    {
                        InvoiceId = invoice.Id,
                        Description = svc.ServiceName,
                        Amount = svc.Amount,
                        Quantity = 1,
                        AppointmentServiceId = svc.Id,
                    });
                }
            }

            // NOTE: the "already settled" check used to live here, evaluated against
            // whatever TotalAmount/PaidAmount happened to be persisted at the moment
            // this row was loaded. That's stale the instant a service was added to
            // the visit since this row was last saved (e.g. via an appointment edit)
            // — TotalAmount hadn't caught up yet, so a genuinely-owed top-up payment
            // got rejected outright as "already settled" instead of being applied.
            // Moved below, after Gross/TotalAmount are re-derived from the live
            // line items (now self-healed above), so the check reflects the real
            // current balance.

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
                invoice.AdditionalCharges = request.AdditionalCharges ?? invoice.AdditionalCharges;
                invoice.AdditionalChargesReason = request.AdditionalChargesReason ?? invoice.AdditionalChargesReason;
            }

            var totalDiscount = invoice.CentreDiscount + invoice.ReferrerDiscount + invoice.InstitutionalDeduction;
            
            // Re-anchor Gross to prevent drift.
            // IMPORTANT: use originalAdditionalCharges (captured before mutation)
            // in the fallback formula, not invoice.AdditionalCharges which was
            // already updated above. This prevents extra charges from vanishing or
            // inflating when there are no Items rows to sum directly.
            var itemsSubtotal = invoice.Items?.Sum(i => i.Quantity * i.Amount) ?? 0;
            if (itemsSubtotal > 0)
            {
                // Source of truth: line items + the new additional charges
                invoice.GrossAmount = itemsSubtotal + invoice.AdditionalCharges;
            }
            else
            {
                // Fallback: strip the OLD additional charges from the old gross,
                // then add the NEW additional charges. This prevents double-counting
                // across successive payment/discount edits.
                var baseGross = (invoice.GrossAmount > 0 ? invoice.GrossAmount : invoice.TotalAmount + invoice.DiscountAmount) - originalAdditionalCharges;
                invoice.GrossAmount = baseGross + invoice.AdditionalCharges;
            }
            
            invoice.DiscountAmount = totalDiscount;
            // GrossAmount ALREADY includes AdditionalCharges, so TotalAmount is simply Gross - Discount
            invoice.TotalAmount = invoice.GrossAmount - totalDiscount;

            // Already-settled check, against the FRESH balance (see note above).
            if (invoice.PaidAmount >= invoice.TotalAmount - 0.01m)
            {
                throw new InvalidOperationException($"Invoice '{invoice.InvoiceId}' is already settled.");
            }

            // Handle Referrer-side adjustment (Differential logic)
            if (invoice.ReferrerDiscount != oldReferrerDiscount)
            {
                // Multi-service appointments carry ONE ReferralCommission row PER
                // SERVICE (see UpdateAppointmentStatusCommand.GenerateBillingOnArrivalAsync),
                // all sharing this invoice's AppointmentId/ReferenceNumber. The
                // invoice-level ReferrerDiscount is a single aggregate figure, so the
                // differential must be spread across the WHOLE group — picking just
                // one row (the old FirstOrDefaultAsync) silently corrupted sibling
                // services' commissions on multi-service visits.
                var commissions = await _context.ReferralCommissions
                    .Where(c =>
                        (c.AppointmentId == invoice.AppointmentId || (c.ReferenceNumber == invoice.InvoiceId && c.ReferenceNumber != null)) &&
                        c.HospitalId == _context.UserContext.HospitalId &&
                        c.DeletedAt == null)
                    .ToListAsync(cancellationToken);

                if (commissions.Count > 0)
                {
                    // Eligible commission pool before any referral concession this cycle.
                    var currentTotal = commissions.Sum(c => c.CommissionAmount);
                    var baseCommission = currentTotal + oldReferrerDiscount;

                    // Over-commission funded by the CENTRE: floor the pool at zero and
                    // shift the excess into the centre discount so the centre — not the
                    // referrer — absorbs it. The patient's total is unchanged (the excess
                    // only moves between the two discount buckets), so just the split +
                    // commission change.
                    if (request.AbsorbExcessToCentre && invoice.ReferrerDiscount > baseCommission)
                    {
                        var excess = invoice.ReferrerDiscount - baseCommission;
                        invoice.CentreDiscount += excess;
                        invoice.ReferrerDiscount = baseCommission;
                        invoice.DiscountAmount = invoice.CentreDiscount + invoice.ReferrerDiscount + invoice.InstitutionalDeduction;
                        invoice.TotalAmount = invoice.GrossAmount - invoice.DiscountAmount;
                        foreach (var c in commissions)
                            c.Remarks = (c.Remarks ?? "") + $" [Excess ₹{excess:0.##} absorbed by centre]";
                    }

                    // Spread the new pool across the group proportionally to each row's
                    // current share, so no single service silently absorbs the whole
                    // invoice-level concession. The last row takes the rounding remainder
                    // so the group sum lands exactly on (baseCommission - ReferrerDiscount).
                    var newTotal = baseCommission - invoice.ReferrerDiscount;
                    var allocated = 0m;
                    for (var i = 0; i < commissions.Count; i++)
                    {
                        var c = commissions[i];
                        decimal rowNew;
                        if (i == commissions.Count - 1)
                        {
                            rowNew = newTotal - allocated;
                        }
                        else
                        {
                            var share = currentTotal != 0 ? c.CommissionAmount / currentTotal : 1m / commissions.Count;
                            rowNew = Math.Round(newTotal * share, 2);
                            allocated += rowNew;
                        }

                        var oldRowAmount = c.CommissionAmount;
                        c.CommissionAmount = rowNew;
                        c.Remarks = (c.Remarks ?? "") + $" [Adj: ₹{oldRowAmount:0.##} -> ₹{rowNew:0.##} (referrer discount ₹{oldReferrerDiscount:0.##} -> ₹{invoice.ReferrerDiscount:0.##})]";

                        // Over-commission concession → the commission is now negative; the
                        // doctor carries that deficit, recovered from future referrals. Audit
                        // the authoriser (the authenticated user) + reason so the credit
                        // decision is traceable. The amount itself is intentionally NOT
                        // clamped to zero — the negative is the whole point.
                        if (c.CommissionAmount < 0)
                        {
                            var deficit = Math.Abs(c.CommissionAmount);
                            var reason = string.IsNullOrWhiteSpace(request.DeficitReason) ? "" : $" — {request.DeficitReason.Trim()}";
                            c.Remarks += $" [DEFICIT ₹{deficit:0.##} authorised by user {_context.UserContext.UserId}{reason}]";
                        }
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

