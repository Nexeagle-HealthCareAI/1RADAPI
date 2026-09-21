using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.RevokeReferralLinks;

/// <summary>
/// Invalidates every doctor-portal link issued so far for a partner. The portal
/// links are signed, stateless tokens that used to live a full year with no way to
/// pull one back (a forwarded WhatsApp message, a lost phone, a doctor who left).
/// Each token now carries a per-partner version; bumping it here makes every older
/// token stop working immediately. A fresh link (sent or copied afterwards) is
/// minted under the new version.
///
/// Partners merged into the target are bumped too — the portal for a merged
/// duplicate shows the primary partner's data, so a duplicate's old link must not
/// outlive the revocation.
/// </summary>
public record RevokeReferralLinksCommand(Guid ReferrerId) : IRequest<RevokeReferralLinksResult>;

public record RevokeReferralLinksResult(Guid ReferrerId, int Version, int LinksInvalidatedFor, DateTime RevokedAt);

public class RevokeReferralLinksCommandHandler : IRequestHandler<RevokeReferralLinksCommand, RevokeReferralLinksResult>
{
    private readonly IApplicationDbContext _context;

    public RevokeReferralLinksCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<RevokeReferralLinksResult> Handle(RevokeReferralLinksCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context required.");

        var registry = await _context.Referrers
            .Where(r => r.HospitalId == hospitalId)
            .Select(r => new { r.ReferrerId, r.MergedIntoId, r.DeletedAt })
            .ToListAsync(ct);
        var target = registry.FirstOrDefault(r => r.ReferrerId == request.ReferrerId && r.DeletedAt == null);
        if (target == null)
            throw new NotFoundException("Partner not found.");

        var mergeMap = registry.ToDictionary(r => r.ReferrerId, r => r.MergedIntoId);
        Guid Root(Guid id)
        {
            var seen = new HashSet<Guid>();
            while (mergeMap.TryGetValue(id, out var next) && next.HasValue && seen.Add(id)) id = next.Value;
            return id;
        }
        var root = Root(request.ReferrerId);
        var ids = registry.Where(r => r.DeletedAt == null && Root(r.ReferrerId) == root).Select(r => r.ReferrerId).ToList();

        var existing = await _context.ReferrerLinkVersions
            .Where(v => v.HospitalId == hospitalId && ids.Contains(v.ReferrerId))
            .ToDictionaryAsync(v => v.ReferrerId, ct);

        var now = DateTime.UtcNow;
        var userId = _context.UserContext.UserId;
        foreach (var id in ids)
        {
            if (existing.TryGetValue(id, out var row))
            {
                row.Version += 1;
                row.RevokedAt = now;
                row.RevokedByUserId = userId;
                // A revoked partner must not be auto-messaged a fresh link behind the
                // centre's back; renewals resume only when a centre sends a link again.
                row.AutoRenew = false;
                row.LastSentExpiresAt = null;
            }
            else
            {
                _context.ReferrerLinkVersions.Add(new ReferrerLinkVersion
                {
                    ReferrerId = id, HospitalId = hospitalId, Version = 1, RevokedAt = now, RevokedByUserId = userId, AutoRenew = false,
                });
            }
        }
        await _context.SaveChangesAsync(ct);

        var newVersion = existing.TryGetValue(request.ReferrerId, out var mine) ? mine.Version : 1;
        return new RevokeReferralLinksResult(request.ReferrerId, newVersion, ids.Count, now);
    }
}
