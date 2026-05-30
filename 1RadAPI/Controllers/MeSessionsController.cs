using System.Security.Claims;
using _1Rad.Application.Features.Sessions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/me/sessions")]
public class MeSessionsController : ControllerBase
{
    private readonly IMediator _mediator;

    public MeSessionsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var sid = ExtractCurrentSessionId();
        var sessions = await _mediator.Send(new GetSessionsQuery(sid));
        return Ok(new { success = true, currentSessionId = sid, sessions });
    }

    [HttpDelete("{sessionId:guid}")]
    public async Task<IActionResult> Revoke(Guid sessionId)
    {
        var ok = await _mediator.Send(new RevokeSessionCommand(sessionId));
        return ok ? Ok(new { success = true }) : NotFound(new { success = false, error = "Session not found." });
    }

    [HttpDelete]
    public async Task<IActionResult> RevokeOthers()
    {
        var sid = ExtractCurrentSessionId();
        if (sid == null)
        {
            return BadRequest(new { success = false, error = "Current session id missing from token." });
        }
        var count = await _mediator.Send(new RevokeOtherSessionsCommand(sid.Value));
        return Ok(new { success = true, revoked = count });
    }

    // Mirrors the lookup the SessionValidationMiddleware does — checks both
    // claim aliases because System.IdentityModel.Tokens.Jwt maps `sid` to
    // ClaimTypes.Sid by default while newer Microsoft.IdentityModel.JsonWebTokens
    // keeps the raw "sid" key. Either should work; we cover both.
    private Guid? ExtractCurrentSessionId()
    {
        var sidStr = User.FindFirst(JwtRegisteredClaimNames.Sid)?.Value
                     ?? User.FindFirst(ClaimTypes.Sid)?.Value;
        return Guid.TryParse(sidStr, out var sid) ? sid : null;
    }
}
