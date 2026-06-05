using _1Rad.Application.Features.Referrers.Queries.GetDoctorPortal;
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

    public PublicReferralController(IMediator mediator, IReferralLinkTokenService tokenService)
    {
        _mediator = mediator;
        _tokenService = tokenService;
    }

    [HttpGet("{referrerId:guid}")]
    public async Task<IActionResult> Get(Guid referrerId, [FromQuery] string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { success = false, error = "Missing link token." });
        if (!_tokenService.Validate(token, referrerId))
            return Unauthorized(new { success = false, error = "This link is invalid or has expired." });

        var result = await _mediator.Send(new GetDoctorPortalQuery(referrerId));
        if (result == null) return NotFound(new { success = false, error = "Referrer not found." });

        return Ok(new { success = true, data = result });
    }
}
