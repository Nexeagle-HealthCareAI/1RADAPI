using System.Linq;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Common;

/// <summary>
/// The canonical "what does this invoice add up to" formula. Every command that
/// changes an invoice's line items, additional charges, or discount amount must
/// call these instead of re-deriving Gross/Total inline — before this existed,
/// the same formula had independently drifted into nine slightly different
/// implementations across the codebase (2026-07-24 architecture audit), some of
/// which omitted AdditionalCharges or the discount-can't-exceed-gross clamp the
/// others had.
///
/// Two steps, not one, because <see cref="RecomputeGross"/>'s fallback branch
/// needs the OLD DiscountAmount/TotalAmount (paired, from the last save) to
/// reconstruct the previous gross — it must run BEFORE the caller decides and
/// assigns the NEW discount. Collapsing this into a single call would force it
/// to guess which one you meant.
/// </summary>
public static class InvoiceTotals
{
    /// <summary>
    /// Recomputes <see cref="Invoice.GrossAmount"/> from the invoice's current
    /// line items (<see cref="Invoice.Items"/> must be loaded) plus
    /// <see cref="Invoice.AdditionalCharges"/>. Call this FIRST, before you
    /// assign a new <see cref="Invoice.DiscountAmount"/> — the fallback branch
    /// (no items to sum) reads the invoice's still-old Discount/Total to
    /// reconstruct the previously-persisted gross, so mutating Discount first
    /// would corrupt that reconstruction.
    /// </summary>
    /// <param name="invoice">The invoice to recompute. Its Items collection must
    /// already be loaded/tracked.</param>
    /// <param name="originalAdditionalCharges">
    /// AdditionalCharges' value BEFORE this request changed it — capture it at
    /// the very top of the handler, before touching
    /// <see cref="Invoice.AdditionalCharges"/>, then assign the new value to
    /// the invoice before calling this. Callers that never touch
    /// AdditionalCharges in the same request should simply pass
    /// <c>invoice.AdditionalCharges</c> unchanged.
    /// </param>
    public static void RecomputeGross(Invoice invoice, decimal originalAdditionalCharges)
    {
        var itemsSubtotal = invoice.Items?.Sum(i => i.Quantity * i.Amount) ?? 0m;
        if (itemsSubtotal > 0)
        {
            invoice.GrossAmount = itemsSubtotal + invoice.AdditionalCharges;
        }
        else
        {
            // No items to sum (not loaded, or a construction path with nothing
            // billable yet) — re-anchor from whatever was last persisted rather
            // than collapsing GrossAmount to zero.
            var baseGross = (invoice.GrossAmount > 0 ? invoice.GrossAmount : invoice.TotalAmount + invoice.DiscountAmount) - originalAdditionalCharges;
            invoice.GrossAmount = baseGross + invoice.AdditionalCharges;
        }
    }

    /// <summary>
    /// Call SECOND, after <see cref="RecomputeGross"/> and after you've decided
    /// what the new discount should be (from the three deduction vectors, a
    /// request scalar, a differential calculation — whatever this command's own
    /// rule is). Clamps the discount so it can never exceed the gross it's
    /// discounting, assigns it, and derives <see cref="Invoice.TotalAmount"/>.
    /// </summary>
    public static void ApplyDiscountAndFinalize(Invoice invoice, decimal discount)
    {
        if (discount > invoice.GrossAmount)
        {
            discount = invoice.GrossAmount;
        }
        invoice.DiscountAmount = discount;
        invoice.TotalAmount = invoice.GrossAmount - discount;
    }
}
