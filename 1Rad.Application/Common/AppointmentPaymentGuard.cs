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
    /// Payment row at all.
    /// </summary>
    public static async Task<bool> HasCollectedPayment(IApplicationDbContext context, Guid appointmentId, CancellationToken cancellationToken)
    {
        return await context.Invoices
            .AnyAsync(i => i.AppointmentId == appointmentId && (i.PaidAmount > 0 || i.Status == "PAID" || i.Status == "PARTIAL"), cancellationToken)
            || await context.Payments.AnyAsync(p => p.Invoice.AppointmentId == appointmentId, cancellationToken);
    }
}
