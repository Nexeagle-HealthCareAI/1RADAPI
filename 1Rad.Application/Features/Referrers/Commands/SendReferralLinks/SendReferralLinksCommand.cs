using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.SendReferralLinks;

// Emails each named referrer their personal doctor-portal link. The link is the
// signed capability URL "{baseUrl}/r/{referrerId}?t={token}". Hospital-scoped.
public record SendReferralLinksCommand(List<Guid> ReferrerIds, string BaseUrl) : IRequest<SendReferralLinksResult>;

public record SendReferralLinksResult(int Sent, int Skipped, List<string> NoEmail);

public class SendReferralLinksCommandHandler : IRequestHandler<SendReferralLinksCommand, SendReferralLinksResult>
{
    private readonly IApplicationDbContext _context;
    private readonly IReferralLinkTokenService _tokens;
    private readonly IEmailService _email;

    public SendReferralLinksCommandHandler(IApplicationDbContext context, IReferralLinkTokenService tokens, IEmailService email)
    {
        _context = context;
        _tokens = tokens;
        _email = email;
    }

    public async Task<SendReferralLinksResult> Handle(SendReferralLinksCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required.");

        var ids = (request.ReferrerIds ?? new List<Guid>()).Distinct().ToList();
        if (ids.Count == 0) return new SendReferralLinksResult(0, 0, new List<string>());

        var referrers = await _context.Referrers
            .Where(r => ids.Contains(r.ReferrerId) && r.HospitalId == hospitalId && r.DeletedAt == null)
            .Select(r => new { r.ReferrerId, r.Name, r.Email })
            .ToListAsync(ct);

        var centreName = await _context.Hospitals
            .Where(h => h.HospitalId == hospitalId)
            .Select(h => h.HospitalName)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(centreName)) centreName = "your diagnostic centre";

        var baseUrl = (request.BaseUrl ?? string.Empty).TrimEnd('/');
        int sent = 0;
        var noEmail = new List<string>();

        foreach (var r in referrers)
        {
            if (string.IsNullOrWhiteSpace(r.Email))
            {
                noEmail.Add(r.Name ?? r.ReferrerId.ToString());
                continue;
            }
            var token = _tokens.Issue(r.ReferrerId);
            var link = $"{baseUrl}/r/{r.ReferrerId}?t={token}";
            try
            {
                await _email.SendEmailAsync(r.Email!, $"Your referral dashboard — {centreName}", BuildBody(r.Name, centreName!, link));
                sent++;
            }
            catch
            {
                noEmail.Add(r.Name ?? r.ReferrerId.ToString());
            }
        }

        return new SendReferralLinksResult(sent, referrers.Count - sent, noEmail);
    }

    private static string BuildBody(string? name, string centre, string link) => $@"
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
