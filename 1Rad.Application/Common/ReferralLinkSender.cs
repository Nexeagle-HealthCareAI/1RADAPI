using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

public sealed record LinkSendOutcome(
    bool Success,
    string Channel,
    string? PartnerName,
    string? MaskedTo,
    string? Error,
    bool NoContact = false,
    bool PartnerMissing = false);

/// <summary>
/// The one place a doctor-portal link is minted, delivered and RECORDED. The manual
/// email / WhatsApp senders, the daily auto-renewal job and the doctor's own "send me
/// a new link" request all go through here, so a link can never be sent without the
/// server remembering when, how, and to which portal — which is what lets renewals
/// happen automatically later.
///
/// Works with or without a signed-in user (the job and the public renewal have none),
/// so every lookup is scoped by an explicit HospitalId with query filters off.
/// </summary>
public sealed class ReferralLinkSender
{
    public const string WhatsApp = "whatsapp";
    public const string Email = "email";

    private readonly IApplicationDbContext _context;
    private readonly IReferralLinkTokenService _tokens;
    private readonly IEmailService _email;
    private readonly ISmsService _sms;

    public ReferralLinkSender(IApplicationDbContext context, IReferralLinkTokenService tokens, IEmailService email, ISmsService sms)
    {
        _context = context;
        _tokens = tokens;
        _email = email;
        _sms = sms;
    }

    /// <param name="autoRenew">Whether the daily job should keep this doctor's link fresh from now on.</param>
    public async Task<LinkSendOutcome> SendAsync(
        Guid hospitalId, Guid referrerId, string channel, string baseUrl, bool autoRenew, CancellationToken ct)
    {
        channel = (channel ?? string.Empty).Trim().ToLowerInvariant();
        if (channel != WhatsApp && channel != Email)
            return new LinkSendOutcome(false, channel, null, null, "Unknown delivery channel.");

        // The link embeds this origin, so it must be a plain http(s) origin we were given
        // deliberately — never something a public caller supplied (see the renewal command).
        var origin = NormalizeBaseUrl(baseUrl);
        if (origin == null)
            return new LinkSendOutcome(false, channel, null, null, "A valid portal address is required to build the link.");

        var referrer = await _context.Referrers.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.ReferrerId == referrerId && r.HospitalId == hospitalId && r.DeletedAt == null)
            .Select(r => new { r.Name, r.Email, r.Contact })
            .FirstOrDefaultAsync(ct);
        if (referrer == null)
            return new LinkSendOutcome(false, channel, null, null, "Partner not found.", PartnerMissing: true);

        var name = referrer.Name ?? referrerId.ToString();
        string? mobile = null, address = null;
        if (channel == WhatsApp)
        {
            mobile = NormalizeMobile(referrer.Contact);
            if (mobile == null) return new LinkSendOutcome(false, channel, name, null, "No usable mobile number on file.", NoContact: true);
        }
        else
        {
            address = referrer.Email?.Trim();
            if (string.IsNullOrEmpty(address)) return new LinkSendOutcome(false, channel, name, null, "No email on file.", NoContact: true);
        }

        var centreName = await _context.Hospitals.AsNoTracking().IgnoreQueryFilters()
            .Where(h => h.HospitalId == hospitalId)
            .Select(h => h.HospitalName)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(centreName)) centreName = "your diagnostic centre";

        var version = await ReferralLinkVersions.GetOneAsync(_context, referrerId, ct);
        var token = _tokens.Issue(referrerId, version);
        var link = $"{origin}/r/{referrerId}?t={token}";

        try
        {
            if (channel == WhatsApp)
                await _sms.SendReferralLinkAsync(mobile!, referrer.Name ?? "Doctor", centreName!, link);
            else
                await _email.SendEmailAsync(address!, $"Your referral dashboard — {centreName}", BuildEmailBody(referrer.Name, centreName!, link));
        }
        catch (Exception ex)
        {
            return new LinkSendOutcome(false, channel, name, null, ex.Message);
        }

        // Remember what we sent so the daily job can renew it before it expires.
        var now = DateTime.UtcNow;
        var row = await _context.ReferrerLinkVersions.IgnoreQueryFilters().FirstOrDefaultAsync(v => v.ReferrerId == referrerId, ct);
        if (row == null)
        {
            row = new ReferrerLinkVersion { ReferrerId = referrerId, HospitalId = hospitalId, Version = version };
            _context.ReferrerLinkVersions.Add(row);
        }
        row.LastSentAt = now;
        row.LastSentChannel = channel;
        row.LastSentBaseUrl = origin;
        row.LastSentExpiresAt = _tokens.TryReadClaims(token, out var claims) ? claims.ExpiresAtUtc : now + _tokens.Ttl;
        row.AutoRenew = autoRenew;
        await _context.SaveChangesAsync(ct);

        return new LinkSendOutcome(true, channel, name, channel == WhatsApp ? MaskMobile(mobile!) : MaskEmail(address!), null);
    }

    /// <summary>An absolute http(s) origin with no trailing slash, or null.</summary>
    public static string? NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? trimmed
            : null;
    }

    // WhatsApp Cloud API wants the full international number, digits only. Stored doctor
    // contacts are bare 10-digit Indian mobiles, so prefix 91; pass any already-prefixed
    // number through. Returns null when there's nothing usable.
    public static string? NormalizeMobile(string? contact)
    {
        var digits = new string((contact ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 10) return "91" + digits;
        if (digits.Length >= 11 && digits.Length <= 15) return digits;
        return null;
    }

    public static string MaskMobile(string mobile) => mobile.Length <= 4 ? "••••" : "••••" + mobile[^4..];

    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "••••";
        return email[0] + "•••" + email[at..];
    }

    private static string BuildEmailBody(string? name, string centre, string link) => $@"
<div style=""font-family:system-ui,Segoe UI,Arial,sans-serif;max-width:560px;margin:0 auto;color:#0f172a"">
  <p style=""font-size:15px"">Dear {System.Net.WebUtility.HtmlEncode(name ?? "Doctor")},</p>
  <p style=""font-size:14px;line-height:1.6;color:#334155"">
    {System.Net.WebUtility.HtmlEncode(centre)} has set up a private dashboard where you can see every patient you've referred — their status, your eligible referral amount, and what's been paid vs outstanding — updated live.
  </p>
  <p style=""text-align:center;margin:28px 0"">
    <a href=""{link}"" style=""background:#0f52ba;color:#fff;text-decoration:none;font-weight:700;font-size:14px;padding:13px 26px;border-radius:10px;display:inline-block"">Open my referral dashboard</a>
  </p>
  <p style=""font-size:12px;color:#94a3b8;line-height:1.6"">This is your personal link — please don't share it. Powered by NexEagle.</p>
</div>";
}
