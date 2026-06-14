using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.SendReferralLinksWhatsApp;

// Sends each named referrer their personal doctor-portal link over WhatsApp via
// the NexEagle WhatsApp Business API. The link is the signed capability URL
// "{baseUrl}/r/{referrerId}?t={token}". Hospital-scoped.
public record SendReferralLinksWhatsAppCommand(List<Guid> ReferrerIds, string BaseUrl) : IRequest<SendReferralLinksWhatsAppResult>;

// Sent = delivered; NoContact = names skipped for a missing mobile; Failed =
// names the gateway rejected; Error = the last gateway message (e.g. template
// not approved) so the UI can show one actionable reason.
public record SendReferralLinksWhatsAppResult(int Sent, List<string> NoContact, List<string> Failed, string? Error);

public class SendReferralLinksWhatsAppCommandHandler : IRequestHandler<SendReferralLinksWhatsAppCommand, SendReferralLinksWhatsAppResult>
{
    private readonly IApplicationDbContext _context;
    private readonly IReferralLinkTokenService _tokens;
    private readonly ISmsService _sms;

    public SendReferralLinksWhatsAppCommandHandler(IApplicationDbContext context, IReferralLinkTokenService tokens, ISmsService sms)
    {
        _context = context;
        _tokens = tokens;
        _sms = sms;
    }

    public async Task<SendReferralLinksWhatsAppResult> Handle(SendReferralLinksWhatsAppCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required.");

        var ids = (request.ReferrerIds ?? new List<Guid>()).Distinct().ToList();
        if (ids.Count == 0) return new SendReferralLinksWhatsAppResult(0, new List<string>(), new List<string>(), null);

        var referrers = await _context.Referrers
            .Where(r => ids.Contains(r.ReferrerId) && r.HospitalId == hospitalId && r.DeletedAt == null)
            .Select(r => new { r.ReferrerId, r.Name, r.Contact })
            .ToListAsync(ct);

        var centreName = await _context.Hospitals
            .Where(h => h.HospitalId == hospitalId)
            .Select(h => h.HospitalName)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(centreName)) centreName = "your diagnostic centre";

        var baseUrl = (request.BaseUrl ?? string.Empty).TrimEnd('/');
        int sent = 0;
        var noContact = new List<string>();
        var failed = new List<string>();
        string? lastError = null;

        foreach (var r in referrers)
        {
            var name = r.Name ?? r.ReferrerId.ToString();
            var mobile = NormalizeMobile(r.Contact);
            if (mobile == null)
            {
                noContact.Add(name);
                continue;
            }

            var token = _tokens.Issue(r.ReferrerId);
            var link = $"{baseUrl}/r/{r.ReferrerId}?t={token}";
            try
            {
                await _sms.SendReferralLinkAsync(mobile, r.Name ?? "Doctor", centreName!, link);
                sent++;
            }
            catch (Exception ex)
            {
                failed.Add(name);
                lastError = ex.Message;
            }
        }

        return new SendReferralLinksWhatsAppResult(sent, noContact, failed, lastError);
    }

    // WhatsApp Cloud API wants the full international number, digits only. Stored
    // doctor contacts are bare 10-digit Indian mobiles, so prefix 91; pass any
    // already-prefixed number through. Returns null when there's nothing usable.
    private static string? NormalizeMobile(string? contact)
    {
        var digits = new string((contact ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 10) return "91" + digits;
        if (digits.Length >= 11 && digits.Length <= 15) return digits;
        return null;
    }
}
