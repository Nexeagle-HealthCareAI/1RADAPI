using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;

namespace _1Rad.Application.Features.Referrers.Commands.SendReferralLinks;

// Emails each named referrer their personal doctor-portal link. The link is the
// signed capability URL "{baseUrl}/r/{referrerId}?t={token}". Hospital-scoped.
//
// Delivery goes through ReferralLinkSender, which also records the send (channel,
// expiry, portal) and switches auto-renewal on for the doctor.
public record SendReferralLinksCommand(List<Guid> ReferrerIds, string BaseUrl) : IRequest<SendReferralLinksResult>;

public record SendReferralLinksResult(int Sent, int Skipped, List<string> NoEmail);

public class SendReferralLinksCommandHandler : IRequestHandler<SendReferralLinksCommand, SendReferralLinksResult>
{
    private readonly IApplicationDbContext _context;
    private readonly ReferralLinkSender _sender;

    public SendReferralLinksCommandHandler(IApplicationDbContext context, ReferralLinkSender sender)
    {
        _context = context;
        _sender = sender;
    }

    public async Task<SendReferralLinksResult> Handle(SendReferralLinksCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required.");

        var ids = (request.ReferrerIds ?? new List<Guid>()).Distinct().ToList();
        if (ids.Count == 0) return new SendReferralLinksResult(0, 0, new List<string>());

        int sent = 0, considered = 0;
        var noEmail = new List<string>();

        foreach (var id in ids)
        {
            var outcome = await _sender.SendAsync(hospitalId, id, ReferralLinkSender.Email, request.BaseUrl, autoRenew: true, ct);
            if (outcome.PartnerMissing) continue;                       // not this centre's partner - ignore, as before
            considered++;
            if (outcome.Success) sent++;
            else noEmail.Add(outcome.PartnerName ?? id.ToString());     // no email on file, or the send failed
        }

        return new SendReferralLinksResult(sent, considered - sent, noEmail);
    }
}
