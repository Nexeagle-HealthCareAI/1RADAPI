using _1Rad.Application.Features.Referrers.Queries.GetReferrers;
using _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;
using _1Rad.Application.Features.Referrers.Queries.GetReferralMatrix;
using _1Rad.Application.Features.Referrers.Queries.GetReferralCommissions;
using _1Rad.Application.Features.Referrers.Commands.CreateReferrer;
using _1Rad.Application.Features.Referrers.Commands.RecordReferralCommission;
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

    [HttpGet("matrix")]
    public async Task<IActionResult> GetMatrix(
        [FromQuery] string period, 
        [FromQuery] DateTime referenceDate, 
        [FromQuery] int weekIndex = 1,
        [FromQuery] string? search = null)
    {
        var result = await _mediator.Send(new GetReferralMatrixQuery(period, referenceDate, weekIndex, search));
        return Ok(result);
    }

    [HttpPost("commissions")]
    public async Task<IActionResult> RecordCommission([FromBody] RecordReferralCommissionCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { commissionId = result });
    }

    [HttpGet("commissions")]
    public async Task<IActionResult> GetCommissions([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate, [FromQuery] Guid? referrerId)
    {
        var result = await _mediator.Send(new GetReferralCommissionsQuery(startDate, endDate, referrerId));
        return Ok(result);
    }
}
