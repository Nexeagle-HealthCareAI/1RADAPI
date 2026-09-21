using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.RenewReferralLinks;

// ═════════════════════════════════════════════════════════════════════════════
//  1. Daily auto-renewal
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Keeps deliberately-sent doctor links fresh. Portal links now expire (90 days by
/// default); for every doctor whose link a centre sent — and whose auto-renewal has not
/// been switched off by a revocation — a new link goes out over the SAME channel to the
/// SAME portal shortly before the old one dies, so an active doctor never hits an
/// expired link.
///
/// Runs with no signed-in user (the daily job), so nothing here depends on the caller's
/// hospital: every lookup is explicit.
///
/// Guard rails:
///   • Master switch  ReferralLinks:AutoRenewEnabled.
///   • Only rows with a recorded send (channel + portal) and AutoRenew on.
///   • Only inside the renewal window; a link that has been dead for over 30 days is left
///     alone (the doctor can still ask for a new one themselves).
///   • At most one send per partner per ~day; success moves the expiry out, so the row
///     drops out of the window and a re-run sends nothing.
///   • A partner with no usable contact (or no longer in the registry) is switched off
///     rather than retried forever; a transient send failure is retried on the next run.
/// </summary>
public record RenewExpiringReferralLinksCommand : IRequest<RenewLinksResult>;

public record RenewLinksResult(int Checked, int Renewed, int Failed, int Disabled);

public class RenewExpiringReferralLinksCommandHandler : IRequestHandler<RenewExpiringReferralLinksCommand, RenewLinksResult>
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan MinGapBetweenSends = TimeSpan.FromHours(20);

    private readonly IApplicationDbContext _context;
    private readonly ReferralLinkSender _sender;
    private readonly ReferralLinkOptions _options;

    public RenewExpiringReferralLinksCommandHandler(IApplicationDbContext context, ReferralLinkSender sender, ReferralLinkOptions options)
    {
        _context = context;
        _sender = sender;
        _options = options;
    }

    public async Task<RenewLinksResult> Handle(RenewExpiringReferralLinksCommand request, CancellationToken ct)
    {
        if (!_options.AutoRenewEnabled) return new RenewLinksResult(0, 0, 0, 0);

        var now = DateTime.UtcNow;
        var horizon = now + _options.RenewBefore;
        var floor = now - GiveUpAfter;
        var lastSentBefore = now - MinGapBetweenSends;

        var due = await _context.ReferrerLinkVersions.IgnoreQueryFilters()
            .Where(v => v.AutoRenew
                        && v.LastSentChannel != null && v.LastSentBaseUrl != null
                        && v.LastSentExpiresAt != null && v.LastSentExpiresAt <= horizon && v.LastSentExpiresAt >= floor
                        && (v.LastSentAt == null || v.LastSentAt < lastSentBefore))
            .ToListAsync(ct);
        if (due.Count == 0) return new RenewLinksResult(0, 0, 0, 0);

        // Only centres that are still active get messages.
        var hospitalIds = due.Select(v => v.HospitalId).Distinct().ToList();
        var activeHospitals = (await _context.Hospitals.IgnoreQueryFilters()
                .Where(h => hospitalIds.Contains(h.HospitalId) && h.Status == "Active")
                .Select(h => h.HospitalId)
                .ToListAsync(ct))
            .ToHashSet();

        int renewed = 0, failed = 0, disabled = 0, considered = 0;
        foreach (var row in due.Where(v => activeHospitals.Contains(v.HospitalId)))
        {
            considered++;
            var outcome = await _sender.SendAsync(row.HospitalId, row.ReferrerId, row.LastSentChannel!, row.LastSentBaseUrl!, autoRenew: true, ct);
            if (outcome.Success) { renewed++; continue; }

            if (outcome.NoContact || outcome.PartnerMissing)
            {
                // Nothing to send to — stop trying until a centre sends a link again.
                row.AutoRenew = false;
                await _context.SaveChangesAsync(ct);
                disabled++;
            }
            else
            {
                failed++;   // gateway hiccup — try again on the next run
            }
        }

        return new RenewLinksResult(considered, renewed, failed, disabled);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  2. Doctor asks for a fresh link (self-service, no login)
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A doctor opens an EXPIRED link and taps "send me a new one". The fresh link goes only
/// to the contact the centre already has on file (WhatsApp / email) — the requester never
/// chooses where it goes and never sees the link, so holding an old link gives an attacker
/// nothing they did not already have. Deliberately takes NO address or portal URL from the
/// request: the portal origin comes from what the centre last used (or config), otherwise
/// anyone with an old link could make us message a doctor a link to a look-alike site.
///
/// Refused when the link is revoked/replaced (the centre pulled it back on purpose), still
/// comfortably valid, or a link was sent to this doctor a moment ago.
/// </summary>
public record RenewReferralLinkSelfServeCommand(Guid ReferrerId, string? Token) : IRequest<SelfServeRenewalResult>;

public record SelfServeRenewalResult(string Channel, string MaskedTo);

public class RenewReferralLinkSelfServeCommandHandler : IRequestHandler<RenewReferralLinkSelfServeCommand, SelfServeRenewalResult>
{
    private readonly IApplicationDbContext _context;
    private readonly IReferralLinkTokenService _tokens;
    private readonly ReferralLinkSender _sender;
    private readonly ReferralLinkOptions _options;

    public RenewReferralLinkSelfServeCommandHandler(IApplicationDbContext context, IReferralLinkTokenService tokens, ReferralLinkSender sender, ReferralLinkOptions options)
    {
        _context = context;
        _tokens = tokens;
        _sender = sender;
        _options = options;
    }

    public async Task<SelfServeRenewalResult> Handle(RenewReferralLinkSelfServeCommand request, CancellationToken ct)
    {
        const string notPossible = "We couldn't send you a new link. Please ask the diagnostic centre to send you one.";

        if (string.IsNullOrWhiteSpace(request.Token) || !_tokens.TryReadClaims(request.Token, out var claims) || claims.ReferrerId != request.ReferrerId)
            throw new BusinessRuleViolationException(notPossible);

        var partner = await _context.Referrers.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.ReferrerId == request.ReferrerId && r.DeletedAt == null)
            .Select(r => new { r.HospitalId })
            .FirstOrDefaultAsync(ct);
        if (partner == null) throw new BusinessRuleViolationException(notPossible);

        var row = await _context.ReferrerLinkVersions.IgnoreQueryFilters().FirstOrDefaultAsync(v => v.ReferrerId == request.ReferrerId, ct);
        var currentVersion = row?.Version ?? 0;

        // A link the centre revoked or replaced must stay dead — renewal is only for links
        // that simply ran out.
        if (claims.Version != currentVersion)
            throw new BusinessRuleViolationException("This link has been replaced. Please ask the diagnostic centre to send you a fresh link.");

        var now = DateTime.UtcNow;
        if (claims.ExpiresAtUtc > now + _options.RenewBefore)
            throw new BusinessRuleViolationException("Your link is still valid — no new link is needed yet.");

        if (row?.LastSentAt != null && row.LastSentAt > now - _options.SelfServeCooldown)
            throw new BusinessRuleViolationException("A fresh link was just sent to you — please check your WhatsApp or email.");

        var baseUrl = row?.LastSentBaseUrl ?? _options.PortalBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new BusinessRuleViolationException(notPossible);

        // Same channel as last time first, then the other one.
        var preferred = row?.LastSentChannel == ReferralLinkSender.Email ? ReferralLinkSender.Email : ReferralLinkSender.WhatsApp;
        var other = preferred == ReferralLinkSender.Email ? ReferralLinkSender.WhatsApp : ReferralLinkSender.Email;

        foreach (var channel in new[] { preferred, other })
        {
            var outcome = await _sender.SendAsync(partner.HospitalId, request.ReferrerId, channel, baseUrl, autoRenew: true, ct);
            if (outcome.Success) return new SelfServeRenewalResult(outcome.Channel, outcome.MaskedTo ?? string.Empty);
        }

        throw new BusinessRuleViolationException("We don't have a working WhatsApp number or email for you. Please ask the diagnostic centre to send you a fresh link.");
    }
}
