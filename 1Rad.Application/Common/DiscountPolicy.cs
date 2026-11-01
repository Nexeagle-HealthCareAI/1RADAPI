using _1Rad.Application.Interfaces;
using _1Rad.Domain.Exceptions;
using Microsoft.Extensions.Configuration;

namespace _1Rad.Application.Common;

/// <summary>
/// Server-side limits on concessions (review B3). Before, the only limits were in the
/// drawer UI: the payment endpoint (open to the front desk) wrote any discount it was
/// sent, and negative values raised the bill.
///
///   • No negative discount, deduction or extra charge, anywhere.
///   • Admins and accountants may give any concession up to the bill.
///   • Everyone else (front desk, custom roles) may give a centre discount plus
///     deduction of at most Billing:FrontDeskDiscountLimitPercent of the bill
///     (default 10%). Above that the change goes through an Edit-payment approval.
///     The referrer discount is not counted: it comes out of the referrer's
///     commission, not the centre's revenue, and is capped by that commission.
/// </summary>
public static class DiscountPolicy
{
    public const decimal DefaultFrontDeskLimitPercent = 10m;
    private static readonly int[] FinanceRoleIds = { 1, 2, 6 }; // AdminDoctor, Admin, Accountant

    public static bool HasFinanceRole(IUserContext user) => user.RoleIds.Any(r => FinanceRoleIds.Contains(r));

    public static decimal FrontDeskLimitPercent(IConfiguration? config)
    {
        var raw = config?["Billing:FrontDeskDiscountLimitPercent"];
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                   System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= 100
            ? v : DefaultFrontDeskLimitPercent;
    }

    public static void RejectNegatives(decimal? centre, decimal? referrer, decimal? deduction, IEnumerable<decimal>? extraCharges = null)
    {
        if (centre < 0) throw new ValidationException("A discount can't be negative.");
        if (referrer < 0) throw new ValidationException("A discount can't be negative.");
        if (deduction < 0) throw new ValidationException("A deduction can't be negative.");
        if (extraCharges != null && extraCharges.Any(a => a < 0))
            throw new ValidationException("An extra charge can't be negative.");
    }

    /// <summary>Throws when the signed-in user may not give this much.</summary>
    public static void CheckLimit(IUserContext user, IConfiguration? config, decimal gross, decimal centre, decimal deduction)
    {
        if (HasFinanceRole(user) || gross <= 0) return;
        var pct = FrontDeskLimitPercent(config);
        var allowed = Math.Round(gross * pct / 100m, 2);
        if (centre + deduction > allowed + 0.009m)
            throw new BusinessRuleViolationException(
                $"The front desk can give up to {pct:0.##}% off (₹{allowed:0.##} on this bill). For more, ask an accountant or admin, or raise a payment-edit request.",
                "DISCOUNT_NEEDS_APPROVAL");
    }
}
