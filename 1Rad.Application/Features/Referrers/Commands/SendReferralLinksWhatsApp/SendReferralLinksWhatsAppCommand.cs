using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;

namespace _1Rad.Application.Features.Referrers.Commands.SendReferralLinksWhatsApp;

// Sends each named referrer their personal doctor-portal link over WhatsApp via
// the NexEagle WhatsApp Business API. The link is the signed capability URL
// "{baseUrl}/r/{referrerId}?t={token}". Hospital-scoped.
//
// Delivery goes through ReferralLinkSender, which also records the send (channel,
// expiry, portal) and switches auto-renewal on for the doctor - so a link a centre
// deliberately sent is kept fresh automatically before it expires.
public record SendReferralLinksWhatsAppCommand(List<Guid> ReferrerIds, string BaseUrl) : IRequest<SendReferralLinksWhatsAppResult>;

// Sent = delivered; NoContact = names skipped for a missing mobile; Failed =
// names the gateway rejected; Error = the last gateway message (e.g. template
// not approved) so the UI can show one actionable reason.
public record SendReferralLinksWhatsAppResult(int Sent, List<string> NoContact, List<string> Failed, string? Error);

public class SendReferralLinksWhatsAppCommandHandler : IRequestHandler<SendReferralLinksWhatsAppCommand, SendReferralLinksWhatsAppResult>
{
    private readonly IApplicationDbContext _context;
    private readonly ReferralLinkSender _sender;

    public SendReferralLinksWhatsAppCommandHandler(IApplicationDbContext context, ReferralLinkSender sender)
    {
        _context = context;
        _sender = sender;
    }

    public async Task<SendReferralLinksWhatsAppResult> Handle(SendReferralLinksWhatsAppCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required.");

        var ids = (request.ReferrerIds ?? new List<Guid>()).Distinct().ToList();
        if (ids.Count == 0) return new SendReferralLinksWhatsAppResult(0, new List<string>(), new List<string>(), null);

        int sent = 0;
        var noContact = new List<string>();
        var failed = new List<string>();
        string? lastError = null;

        foreach (var id in ids)
        {
            var outcome = await _sender.SendAsync(hospitalId, id, ReferralLinkSender.WhatsApp, request.BaseUrl, autoRenew: true, ct);
            if (outcome.PartnerMissing) continue;                       // not this centre's partner - ignore, as before
            var name = outcome.PartnerName ?? id.ToString();
            if (outcome.Success) sent++;
            else if (outcome.NoContact) noContact.Add(name);
            else { failed.Add(name); lastError = outcome.Error; }
        }

        return new SendReferralLinksWhatsAppResult(sent, noContact, failed, lastError);
    }
}
