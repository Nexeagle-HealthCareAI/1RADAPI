using _1Rad.Application.Features.PublicTracking;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

// Patient-facing endpoints for the /track QR. No auth required, but every
// request must present a signed token bound to the requested appointment id.
// The token is short-lived (1 year by default) and signed with the same
// secret as our JWT — losing the key rotates every existing token in one go.
[ApiController]
[AllowAnonymous]
[Route("api/v1/public/tracking")]
public class PublicTrackingController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITrackingTokenService _tokenService;

    public PublicTrackingController(IMediator mediator, ITrackingTokenService tokenService)
    {
        _mediator = mediator;
        _tokenService = tokenService;
    }

    // Single combined endpoint: returns the tracker-safe appointment snapshot
    // and (only if finalized) the report. Saves the public page a second
    // round-trip — important on patient phones where battery + signal vary.
    [HttpGet("{appointmentId:guid}")]
    public async Task<IActionResult> Get(Guid appointmentId, [FromQuery] string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unauthorized(new { success = false, error = "Missing tracking token." });
        }
        if (!_tokenService.Validate(token, appointmentId))
        {
            return Unauthorized(new { success = false, error = "Tracking token is invalid or expired." });
        }

        var result = await _mediator.Send(new GetTrackingDataQuery(appointmentId));
        if (result == null) return NotFound(new { success = false, error = "Appointment not found." });

        return Ok(new { success = true, data = result });
    }
}

// Authenticated companion endpoint used by the reporting / booking surfaces
// to mint a token when they generate the QR code. Lives under the existing
// /appointments route to keep the public path clean of anything privileged.
[ApiController]
[Authorize]
[Route("api/v1/appointments")]
public class AppointmentTrackingTokenController : ControllerBase
{
    private readonly ITrackingTokenService _tokenService;

    public AppointmentTrackingTokenController(ITrackingTokenService tokenService)
    {
        _tokenService = tokenService;
    }

    [HttpGet("{id:guid}/tracking-token")]
    public IActionResult Issue(Guid id)
    {
        var token = _tokenService.Issue(id);
        return Ok(new { success = true, token });
    }
}
