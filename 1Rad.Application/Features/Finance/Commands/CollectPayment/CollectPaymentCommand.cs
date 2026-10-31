using MediatR;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
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
                    var newItem = new InvoiceItem
                    {
                        InvoiceId = invoice.Id,
                        Description = svc.ServiceName,
                        Amount = svc.Amount,
                        Quantity = 1,
                        AppointmentServiceId = svc.Id,
                    };
                    invoice.Items.Add(newItem);
                    // invoice.Items.Add() alone doesn't reliably track a new item
                    // as Added onto an ALREADY-TRACKED invoice (its Id is a
                    // non-default Guid set at construction, not by EF) — it can
                    // end up Modified, and SaveChanges then issues a no-op UPDATE
                    // against a row that was never inserted instead of an INSERT,
                    // silently dropping the self-healed line with no error — on
                    // this exact "last gate before money changes hands" step.
                    _context.Entry(newItem).State = Microsoft.EntityFrameworkCore.EntityState.Added;
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

            // Canonical recompute (Common/InvoiceTotals.cs) — see that file for why
            // this is two calls, not one: RecomputeGross must run before the new
            // discount is assigned, since its fallback branch needs the invoice's
            // still-old Discount/Total to reconstruct the previously-persisted gross.
            InvoiceTotals.RecomputeGross(invoice, originalAdditionalCharges);
            InvoiceTotals.ApplyDiscountAndFinalize(invoice, totalDiscount);

            // Already-settled check, against the FRESH balance (see note above).
            // Review B17: a bill discounted to ₹0 has nothing to collect but still has to
            // reach PAID — it used to hit this check and stay PENDING forever. Only a bill
            // with a real balance that is already covered is "already settled".
            var zeroTotal = invoice.TotalAmount <= 0.01m;
            if (!zeroTotal && invoice.PaidAmount >= invoice.TotalAmount - 0.01m)
            {
                throw new ConflictException($"Bill {invoice.InvoiceId} is already settled.");
            }
            if (zeroTotal && request.Amount > 0.009m && string.Equals(invoice.Status, "PAID", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConflictException($"Bill {invoice.InvoiceId} is already settled.");
            }

            // Referrer-side adjustment: the commission moves by the change in the
            // referrer discount, never touching PAID rows (Common/ReferrerDiscountCommission).
            {
                var reason = string.IsNullOrWhiteSpace(request.DeficitReason) ? "" : $" — {request.DeficitReason.Trim()}";
                await ReferrerDiscountCommission.ApplyAsync(
                    _context, invoice, oldReferrerDiscount, request.AbsorbExcessToCentre,
                    $"at collection by user {_context.UserContext.UserId}{reason}", cancellationToken);
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

            // Retry-and-refresh on concurrency conflicts, mirroring
            // UpdateAppointmentCommand's handling of the same failure mode. The
            // only RowVersion-tracked entity this handler ever writes is
            // AppointmentService (the free→paid rollback above) — everything
            // else it touches (Invoice, InvoiceItem, Payment, CreditTransaction,
            // ReferralCommission) carries no OCC token. So a conflict here can
            // only mean a concurrent appointment edit (e.g. adding/removing a
            // test) bumped an AppointmentService row's RowVersion after this
            // handler read it — a spurious clash, not a real lost update, since
            // that edit changes service composition while this handler only
            // flips IsFree/UpdatedAt. Refresh the stale token and re-apply
            // rather than bubbling a 409 the biller can't act on. Bounded to
            // avoid a loop.
            const int maxConcurrencyAttempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (DbUpdateConcurrencyException ex) when (attempt < maxConcurrencyAttempts)
                {
                    foreach (var entry in ex.Entries)
                    {
                        var dbValues = await entry.GetDatabaseValuesAsync(cancellationToken);
                        if (dbValues == null)
                            entry.State = EntityState.Detached;          // row was deleted — drop just this change
                        else
                            entry.OriginalValues.SetValues(dbValues);     // refresh token; our change still wins
                    }
                }
            }
            return true;
        }
        catch (Exception)
        {
            throw;
        }
    }
}

