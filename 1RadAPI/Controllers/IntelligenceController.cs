using _1Rad.Application.Features.Appointments.Queries.GetStrategicOutlook;
using _1Rad.Application.Features.Referrers.Queries.ExportReferralIntelligence;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/intelligence")]
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

    [HttpGet("export")]
    public async Task<FileResult> GetIntelligenceExport([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate, [FromQuery] bool allTime = false)
    {
        var fileContent = await _mediator.Send(new ExportReferralIntelligenceQuery(startDate, endDate, allTime));
        var fileName = $"1Rad_Intelligence_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
        return File(fileContent, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}
