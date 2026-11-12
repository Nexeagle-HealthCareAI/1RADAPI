using Microsoft.Extensions.Configuration;

namespace _1Rad.Application.Common;

/// <summary>
/// TDS on referral payouts (review R10). Defaults follow the Income-tax Act, 2025
/// as in force from 1 April 2026 (section 393(1)): commission — 2% once a payee's
/// total for the financial year passes ₹20,000; professional fees — 10% past
/// ₹50,000; 20% when the payee has no PAN. They are settings
/// (ReferralTds:Commission:Rate, …) so the centre's CA can change them without a
/// release. Amounts are rounded to whole rupees.
///
/// Once a payee crosses the threshold, tax is due on the whole year's amount, so
/// the first payout over the line also catches up on that year's earlier untaxed
/// payouts (never more than this payout itself).
/// </summary>
public static class ReferralTds
{
    public const string Commission = "COMMISSION";
    public const string Professional = "PROFESSIONAL";
    public const string None = "NONE";

    public sealed record Rule(string Code, string Label, decimal Rate, decimal Threshold, string Reference);
    public sealed record Result(decimal Rate, decimal Tds, decimal Base, decimal YearTotal, bool OverThreshold, string Note);

    public static decimal NoPanRate(IConfiguration? c) => Read(c, "ReferralTds:NoPanRate", 20m);

    public static Rule? RuleFor(string? category, IConfiguration? c) => (category ?? None).Trim().ToUpperInvariant() switch
    {
        Commission => new Rule(Commission, "Commission",
            Read(c, "ReferralTds:Commission:Rate", 2m), Read(c, "ReferralTds:Commission:Threshold", 20000m),
            c?["ReferralTds:Commission:Reference"] ?? "s.393(1) Table Sl. 1(ii) — formerly s.194H"),
        Professional => new Rule(Professional, "Professional fees",
            Read(c, "ReferralTds:Professional:Rate", 10m), Read(c, "ReferralTds:Professional:Threshold", 50000m),
            c?["ReferralTds:Professional:Reference"] ?? "s.393(1) Table Sl. 6(iii) — formerly s.194J"),
        _ => null,
    };

    /// <param name="yearBefore">Taxable amount already paid to this payee this financial year.</param>
    /// <param name="yearTaxedBefore">Of that, the part TDS was already deducted on.</param>
    /// <param name="thisPayout">Taxable amount of this payout (gross − deficit recovered).</param>
    public static Result Compute(Rule? rule, bool hasPan, decimal yearBefore, decimal yearTaxedBefore, decimal thisPayout, IConfiguration? c)
    {
        if (rule == null || thisPayout <= 0)
            return new Result(0, 0, 0, yearBefore + Math.Max(0, thisPayout), false, "No TDS category chosen.");
        var yearTotal = yearBefore + thisPayout;
        if (yearTotal <= rule.Threshold)
            return new Result(0, 0, 0, yearTotal, false,
                $"Below the ₹{rule.Threshold:N0} yearly limit for {rule.Label.ToLowerInvariant()} (₹{yearTotal:N0} this year) — no TDS yet.");
        var rate = hasPan ? rule.Rate : Math.Max(rule.Rate, NoPanRate(c));
        var untaxedEarlier = Math.Max(0, yearBefore - yearTaxedBefore);
        var basis = thisPayout + untaxedEarlier;
        var tds = Math.Min(Math.Round(basis * rate / 100m, 0, MidpointRounding.AwayFromZero), thisPayout);
        var note = $"{rule.Label}: {rate:0.##}% on ₹{basis:N0}" +
                   (untaxedEarlier > 0 ? $" (includes ₹{untaxedEarlier:N0} paid earlier this year without TDS)" : "") +
                   (hasPan ? "" : " — no PAN on file, so the higher rate applies") + $". {rule.Reference}.";
        return new Result(rate, tds, basis, yearTotal, true, note);
    }

    public static bool IsValidPan(string? pan) =>
        pan != null && System.Text.RegularExpressions.Regex.IsMatch(pan, "^[A-Z]{5}[0-9]{4}[A-Z]$");

    private static decimal Read(IConfiguration? c, string key, decimal fallback) =>
        decimal.TryParse(c?[key], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : fallback;
}
