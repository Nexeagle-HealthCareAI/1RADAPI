using _1Rad.Application.Features.Referrers.Queries.GetReferrers;
using _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;
using _1Rad.Application.Features.Referrers.Commands.CreateReferrer;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/referrers")]
public class ReferrersController : ControllerBase
{
    private readonly IMediator _mediator;

    public ReferrersController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? search)
    {
        var result = await _mediator.Send(new GetReferrersQuery(search));
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReferrerCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { referrerId = result });
    }

    [HttpGet("intelligence")]
    public async Task<IActionResult> GetIntelligence([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate, [FromQuery] Guid? referrerId)
    {
        var result = await _mediator.Send(new GetReferralIntelligenceQuery(startDate, endDate, referrerId));
        return Ok(result);
    }
}
