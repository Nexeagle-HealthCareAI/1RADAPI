using _1Rad.Application.Features.Patients.Queries.GetPatients;
using _1Rad.Application.Features.Patients.Commands.CreatePatient;
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
    public async Task<IActionResult> Get([FromQuery] string? search)
    {
        var result = await _mediator.Send(new GetPatientsQuery(search));
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePatientCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { patientId = result });
    }
}
