using _1Rad.Application.Features.Appointments.Queries.GetAppointments;
using _1Rad.Application.Features.Appointments.Commands.CreateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;
using _1Rad.Application.Features.Appointments.Commands.ImportAppointments;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using _1Rad.Application.Features.Appointments.Commands.UpdateReportProgress;

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

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var result = await _mediator.Send(new GetAppointmentByIdQuery(id));
        return result != null ? Ok(result) : NotFound();
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
        if (!result.Success && result.Message == "Appointment not found.")
        {
            return NotFound();
        }
        return Ok(result);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAppointmentCommand command)
    {
        if (id != command.AppointmentId) return BadRequest("Appointment ID mismatch.");
        var result = await _mediator.Send(command);
        return result ? Ok() : NotFound();
    }

    [HttpPut("{id}/operations-status")]
    public async Task<IActionResult> UpdateOperationsStatus(Guid id, [FromBody] UpdateReportProgressCommand command)
    {
        if (id != command.AppointmentId) return BadRequest("Appointment ID mismatch.");
        var result = await _mediator.Send(command);
        return result ? Ok(new { success = true }) : NotFound();
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import(IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest("Mission log file is missing or corrupted.");
        
        using (var stream = file.OpenReadStream())
        {
            var result = await _mediator.Send(new ImportAppointmentsCommand(stream));
            return Ok(result);
        }
    }
}
