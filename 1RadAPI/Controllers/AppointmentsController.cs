using _1Rad.Application.Features.Appointments.Queries.GetAppointments;
using _1Rad.Application.Features.Appointments.Commands.CreateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/appointments")]
public class AppointmentsController : ControllerBase
{
    private readonly IMediator _mediator;

    public AppointmentsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? search, [FromQuery] string? status)
    {
        var result = await _mediator.Send(new GetAppointmentsQuery(search, status));
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAppointmentCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { appointmentId = result });
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] string status)
    {
        var result = await _mediator.Send(new UpdateAppointmentStatusCommand(id, status));
        return result ? Ok() : NotFound();
    }
}
