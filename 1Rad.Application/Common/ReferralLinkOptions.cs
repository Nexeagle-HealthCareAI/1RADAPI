using Microsoft.Extensions.Configuration;

namespace _1Rad.Application.Common;

/// <summary>
/// Lifetime + renewal policy for the public doctor-portal links. Read from the
/// "ReferralLinks" configuration section; every value has a safe default and is
/// clamped so a typo in config can never mint immortal (or instantly-dead) links.
///
///   ReferralLinks:TtlDays                 how long a NEW link works               (default 90, 7-365)
///   ReferralLinks:RenewBeforeDays         renew this many days before expiry      (default 14)
///   ReferralLinks:AutoRenewEnabled        master switch for the daily renewal     (default true)
///   ReferralLinks:SelfServeCooldownHours  min gap between sends to one partner    (default 6)
///   ReferralLinks:PortalBaseUrl           fallback portal origin when no send has ever
///                                         recorded one - NEVER taken from a request
///
/// Links already issued keep the expiry they were minted with (up to 365 days).
/// </summary>
public sealed class ReferralLinkOptions
{
    public int TtlDays { get; init; } = 90;
    public int RenewBeforeDays { get; init; } = 14;
    public bool AutoRenewEnabled { get; init; } = true;
    public int SelfServeCooldownHours { get; init; } = 6;
    public string? PortalBaseUrl { get; init; }

    public TimeSpan Ttl => TimeSpan.FromDays(TtlDays);
    public TimeSpan RenewBefore => TimeSpan.FromDays(RenewBeforeDays);
    public TimeSpan SelfServeCooldown => TimeSpan.FromHours(SelfServeCooldownHours);

    public static ReferralLinkOptions From(IConfiguration? configuration)
    {
        static int Int(IConfiguration? c, string key, int fallback, int min, int max)
            => int.TryParse(c?[key], out var v) ? Math.Clamp(v, min, max) : fallback;

        var ttl = Int(configuration, "ReferralLinks:TtlDays", 90, 7, 365);
        var baseUrl = configuration?["ReferralLinks:PortalBaseUrl"];
        return new ReferralLinkOptions
        {
            TtlDays = ttl,
            // Renewing must happen well inside the lifetime, or a link would be
            // re-sent the moment it is minted.
            RenewBeforeDays = Int(configuration, "ReferralLinks:RenewBeforeDays", Math.Min(14, ttl / 2), 1, Math.Max(1, ttl / 2)),
            AutoRenewEnabled = !string.Equals(configuration?["ReferralLinks:AutoRenewEnabled"], "false", StringComparison.OrdinalIgnoreCase),
            SelfServeCooldownHours = Int(configuration, "ReferralLinks:SelfServeCooldownHours", 6, 1, 72),
            PortalBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/'),
        };
    }
}
