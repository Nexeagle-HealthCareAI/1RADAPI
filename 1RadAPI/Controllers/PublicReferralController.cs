using _1Rad.Application.Features.Referrers.Commands.RenewReferralLinks;
using _1Rad.Application.Features.Referrers.Commands.UpdateDoctorProfile;
using _1Rad.Application.Features.Referrers.Queries.GetDoctorPortal;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

// Doctor-facing portal endpoint for the /r/{id} link. No auth — access is the
// signed capability token bound to the referrer id (mirrors PublicTracking).
[ApiController]
[AllowAnonymous]
[Route("api/v1/public/referral")]
public class PublicReferralController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IReferralLinkTokenService _tokenService;
    private readonly IApplicationDbContext _context;

    public PublicReferralController(IMediator mediator, IReferralLinkTokenService tokenService, IApplicationDbContext context)
    {
        _mediator = mediator;
        _tokenService = tokenService;
        _context = context;
    }

    // Signature + expiry + the partner's CURRENT link version (a revoked link fails
    // here even though its signature and expiry are still good).
    private Task<ReferralLinkState> LinkStateAsync(string token, Guid referrerId)
        => ReferralLinkAccess.CheckAsync(_context, _tokenService, token, referrerId, HttpContext.RequestAborted);

    // 401 body for a link that is not usable. A merely EXPIRED link is flagged renewable so
    // the portal can offer "send me a new link" (which goes to the contact on file).
    private IActionResult LinkRejected(ReferralLinkState state) => state == ReferralLinkState.Expired
        ? Unauthorized(new
        {
            success = false,
            code = "LINK_EXPIRED",
            canRenew = true,
            error = "Your link has expired. Tap below and we will send a fresh one to the WhatsApp number or email the diagnostic centre has for you.",
        })
        : Unauthorized(new
        {
            success = false,
            code = "LINK_INVALID",
            canRenew = false,
            error = "This link is no longer valid - it has been replaced. Please ask the diagnostic centre to send you a fresh link.",
        });

    [HttpGet("{referrerId:guid}")]
    public async Task<IActionResult> Get(Guid referrerId, [FromQuery] string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { success = false, error = "Missing link token." });
        var state = await LinkStateAsync(token, referrerId);
        if (state != ReferralLinkState.Valid) return LinkRejected(state);

        var result = await _mediator.Send(new GetDoctorPortalQuery(referrerId));
        if (result == null) return NotFound(new { success = false, error = "Referrer not found." });

        return Ok(new { success = true, data = result });
    }

    // An EXPIRED link asks for a fresh one. The new link is sent only to the WhatsApp number /
    // email the centre already has for this doctor (never to an address in the request), and
    // the request carries no portal URL - see RenewReferralLinkSelfServeCommand.
    [HttpPost("{referrerId:guid}/renew")]
    public async Task<IActionResult> Renew(Guid referrerId, [FromQuery] string? token)
    {
        var result = await _mediator.Send(new RenewReferralLinkSelfServeCommand(referrerId, token));
        return Ok(new { success = true, channel = result.Channel, maskedTo = result.MaskedTo });
    }

    // The doctor updates their own profile (location / specialty / degree) from
    // the portal. Same capability-token gate as the read — the token is bound to
    // this referrer id, so a doctor can only ever edit their own record.
    public sealed record UpdateProfileBody(string? Name, string? Location, string? Specialty, string? Degree, string? Email, string? Contact);

    [HttpPut("{referrerId:guid}/profile")]
    public async Task<IActionResult> UpdateProfile(Guid referrerId, [FromQuery] string? token, [FromBody] UpdateProfileBody body)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { success = false, error = "Missing link token." });
        var state = await LinkStateAsync(token, referrerId);
        if (state != ReferralLinkState.Valid) return LinkRejected(state);

        var ok = await _mediator.Send(new UpdateDoctorProfileCommand(referrerId, body?.Name, body?.Location, body?.Specialty, body?.Degree, body?.Email, body?.Contact));
        if (!ok) return NotFound(new { success = false, error = "Referrer not found." });

        return Ok(new { success = true });
    }
}
