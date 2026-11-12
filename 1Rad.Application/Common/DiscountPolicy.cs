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
///   • Admins may give any concession up to the bill.
///   • Accountants may too, unless the centre set Billing:AccountantDiscountLimitPercent
///     (Money ▸ Setup, migration 116).
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

    private static readonly int[] AdminRoleIds = { 1, 2 };   // AdminDoctor, Admin
    private const int AccountantRoleId = 6;

    public static decimal? AccountantLimitPercent(IConfiguration? config)
    {
        var raw = config?["Billing:AccountantDiscountLimitPercent"];
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                   System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= 100
            ? v : null;
    }

    /// <summary>
    /// The most this user may take off a bill, as % of the bill; null = no limit.
    /// Admins: none. Accountants: none unless the centre set one (Money ▸ Setup,
    /// migration 116). Everyone else: the front-desk limit.
    /// </summary>
    public static decimal? LimitPercentFor(IUserContext user, IConfiguration? config)
    {
        if (user.RoleIds.Any(r => AdminRoleIds.Contains(r))) return null;
        if (user.RoleIds.Contains(AccountantRoleId)) return AccountantLimitPercent(config);
        return FrontDeskLimitPercent(config);
    }

    /// <summary>Throws when the signed-in user may not give this much.</summary>
    public static void CheckLimit(IUserContext user, IConfiguration? config, decimal gross, decimal centre, decimal deduction)
    {
        if (gross <= 0) return;
        var pct = LimitPercentFor(user, config);
        if (pct == null) return;
        var allowed = Math.Round(gross * pct.Value / 100m, 2);
        if (centre + deduction > allowed + 0.009m)
            throw new BusinessRuleViolationException(
                $"You can give up to {pct:0.##}% off (₹{allowed:0.##} on this bill). For more, send it to the owner for approval.",
                "DISCOUNT_NEEDS_APPROVAL");
    }
}
