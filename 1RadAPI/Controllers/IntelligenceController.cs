using _1Rad.Application.Features.Appointments.Queries.GetStrategicOutlook;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class IntelligenceController : ControllerBase
{
    private readonly IMediator _mediator;

    public IntelligenceController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("outlook")]
    public async Task<ActionResult<StrategicOutlookDto>> GetStrategicOutlook([FromQuery] DateTime? referenceDate)
    {
        return Ok(await _mediator.Send(new GetStrategicOutlookQuery(referenceDate)));
    }
}
