using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentServiceStatus;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentServiceNotes;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers.Appointments;

/// <summary>
/// Per-service operations within a multi-service appointment.
/// Handles individual service status transitions (ARRIVED → SCANNED → DELIVERED)
/// and technician notes — completely separate from appointment-level scheduling.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/appointments")]
[_1RadAPI.Authorization.RequiresModule(_1Rad.Domain.Constants.ModuleConstants.Ris)]
public class AppointmentServiceController : ControllerBase
{
    private readonly IMediator _mediator;

    public AppointmentServiceController(IMediator mediator)
    {
        _mediator = mediator;
    }

    // Per-service status transition (multi-service rollout step 5).
    // Body: { "status": "SCANNED" }. Updates the AppointmentService row's
    // status + per-service TAT timestamps and recomputes the parent
    // Appointment's rollup so the worklist's status pill, on-premises
    // clock and the scan→delivery TAT stay correct.
    public sealed record UpdateServiceStatusBody(string Status);

    [HttpPatch("{id}/services/{serviceId}/status")]
    public async Task<IActionResult> UpdateServiceStatus(
        Guid id, Guid serviceId, [FromBody] UpdateServiceStatusBody body)
    {
        var result = await _mediator.Send(
            new UpdateAppointmentServiceStatusCommand(id, serviceId, body?.Status ?? string.Empty));
        if (!result.Success && result.Message == "Service not found on this appointment.")
            return NotFound(result);
        if (!result.Success && result.NotAllowed)
            return BadRequest(result);
        return Ok(result);
    }

    // Per-service notes. Body: { "notes": "..." }. Null/empty clears
    // the field. Stored on AppointmentService.TechnicianComments —
    // separate from the visit-level Appointment.DelayReason so a CT
    // can carry "patient asked to come back tomorrow" while the
    // X-ray on the same visit stays clean.
    public sealed record UpdateServiceNotesBody(string? Notes);

    [HttpPatch("{id}/services/{serviceId}/notes")]
    public async Task<IActionResult> UpdateServiceNotes(
        Guid id, Guid serviceId, [FromBody] UpdateServiceNotesBody body)
    {
        var result = await _mediator.Send(
            new UpdateAppointmentServiceNotesCommand(id, serviceId, body?.Notes));
        if (!result.Success && result.Message == "Service not found on this appointment.")
            return NotFound(result);
        return Ok(result);
    }
}
