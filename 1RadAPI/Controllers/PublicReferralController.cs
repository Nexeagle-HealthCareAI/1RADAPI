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
    private Task<bool> IsLinkValidAsync(string token, Guid referrerId)
        => ReferralLinkAccess.IsValidAsync(_context, _tokenService, token, referrerId, HttpContext.RequestAborted);

    [HttpGet("{referrerId:guid}")]
    public async Task<IActionResult> Get(Guid referrerId, [FromQuery] string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { success = false, error = "Missing link token." });
        if (!await IsLinkValidAsync(token, referrerId))
            return Unauthorized(new { success = false, error = "This link is no longer valid — it has expired or been replaced. Please ask the diagnostic centre to send you a fresh link." });

        var result = await _mediator.Send(new GetDoctorPortalQuery(referrerId));
        if (result == null) return NotFound(new { success = false, error = "Referrer not found." });

        return Ok(new { success = true, data = result });
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
        if (!await IsLinkValidAsync(token, referrerId))
            return Unauthorized(new { success = false, error = "This link is no longer valid — it has expired or been replaced. Please ask the diagnostic centre to send you a fresh link." });

        var ok = await _mediator.Send(new UpdateDoctorProfileCommand(referrerId, body?.Name, body?.Location, body?.Specialty, body?.Degree, body?.Email, body?.Contact));
        if (!ok) return NotFound(new { success = false, error = "Referrer not found." });

        return Ok(new { success = true });
    }
}
