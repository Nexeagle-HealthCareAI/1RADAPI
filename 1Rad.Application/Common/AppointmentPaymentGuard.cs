using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// Gates edits that must go through admin approval once real money is on the
/// books for a visit (reassigning the referrer, cancelling an arrived
/// appointment) — before this existed, the identical predicate was
/// copy-pasted verbatim across three separate commands (2026-07-24
/// architecture audit), which meant a change to the rule had to be
/// remembered and applied three times.
/// </summary>
public static class AppointmentPaymentGuard
{
    /// <summary>
    /// True if this appointment's visit has any money on the books — a live
    /// invoice that's PAID/PARTIAL or carries a positive PaidAmount, OR any
    /// net positive Payment total (refund rows count against it).
    /// </summary>
    public static async Task<bool> HasCollectedPayment(IApplicationDbContext context, Guid appointmentId, CancellationToken cancellationToken)
    {
        return await context.Invoices
            .AnyAsync(i => i.AppointmentId == appointmentId && (i.PaidAmount > 0 || i.Status == "PAID" || i.Status == "PARTIAL"), cancellationToken)
            // Net of refunds: a visit whose money was fully returned has nothing on the books.
            || await context.Payments.Where(p => p.Invoice.AppointmentId == appointmentId)
                   .SumAsync(p => p.Amount, cancellationToken) > 0.009m;
    }
}
