using _1Rad.Application.Features.Patients.Queries.GetPatients;
using _1Rad.Application.Features.Patients.Commands.CreatePatient;
using _1Rad.Application.Features.Patients.Commands.UpdatePatient;
using _1Rad.Application.Features.Patients.Queries.GetPatientTimeline;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/patients")]
public class PatientsController : ControllerBase
{
    private readonly IMediator _mediator;

    public PatientsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] DateTime? updatedAfter,
        [FromQuery] bool includeDeleted = false)
    {
        var result = await _mediator.Send(new GetPatientsQuery(search, startDate, endDate, updatedAfter, includeDeleted));
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePatientCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { patientId = result });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePatientCommand command)
    {
        if (id != command.PatientId) return BadRequest("Identity mismatch.");
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    [HttpGet("{id}/timeline")]
    public async Task<IActionResult> GetTimeline(Guid id)
    {
        var result = await _mediator.Send(new GetPatientTimelineQuery(id));
        return Ok(new { success = true, data = result });
    }
}
