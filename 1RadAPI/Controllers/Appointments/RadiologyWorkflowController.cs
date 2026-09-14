using _1Rad.Application.Features.Appointments.Commands.UpdateReportProgress;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers.Appointments;

/// <summary>
/// Radiology-specific workflow: report progress tracking and TAT management.
/// Isolated from scheduling so radiology department changes don't touch
/// front-desk booking logic.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/appointments")]
[_1RadAPI.Authorization.RequiresModule(_1Rad.Domain.Constants.ModuleConstants.Ris)]
public class RadiologyWorkflowController : ControllerBase
{
    private readonly IMediator _mediator;

    public RadiologyWorkflowController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPut("{id}/operations-status")]
    public async Task<IActionResult> UpdateOperationsStatus(
        Guid id, [FromBody] UpdateReportProgressCommand command)
    {
        if (id != command.AppointmentId) return BadRequest("Appointment ID mismatch.");
        var result = await _mediator.Send(command);
        return result ? Ok(new { success = true }) : NotFound();
    }
}
