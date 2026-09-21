using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferralLinkStatus;

/// <summary>
/// The Doctor Links tab's view of each partner's portal link: when it was last sent, over
/// which channel, when that link expires, and whether it renews on its own. Only partners
/// that have ever been sent or revoked have a row; the rest have simply never been sent a
/// link, so they are absent (the UI reads absence as "not sent yet").
/// </summary>
public record GetReferralLinkStatusQuery : IRequest<List<ReferralLinkStatusDto>>;

public record ReferralLinkStatusDto(
    Guid ReferrerId,
    int Version,
    DateTime? LastSentAt,
    string? LastSentChannel,
    DateTime? LastSentExpiresAt,
    bool AutoRenew,
    DateTime? RevokedAt);

public class GetReferralLinkStatusQueryHandler : IRequestHandler<GetReferralLinkStatusQuery, List<ReferralLinkStatusDto>>
{
    private readonly IApplicationDbContext _context;

    public GetReferralLinkStatusQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<List<ReferralLinkStatusDto>> Handle(GetReferralLinkStatusQuery request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty) return new List<ReferralLinkStatusDto>();

        return await _context.ReferrerLinkVersions
            .AsNoTracking()
            .Where(v => v.HospitalId == hospitalId)
            .Select(v => new ReferralLinkStatusDto(v.ReferrerId, v.Version, v.LastSentAt, v.LastSentChannel, v.LastSentExpiresAt, v.AutoRenew, v.RevokedAt))
            .ToListAsync(ct);
    }
}
