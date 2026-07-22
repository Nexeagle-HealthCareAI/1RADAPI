using _1Rad.Application.Features.Appointments.Queries.GetAppointments;
using _1Rad.Application.Features.Appointments.Commands.CreateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;
using _1Rad.Application.Features.Appointments.Commands.ChangeReferrer;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers.Appointments;

/// <summary>
/// Core appointment scheduling — CRUD and status lifecycle.
/// Referrer changes are included here since they are a scheduling-level concern.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/appointments")]
[_1RadAPI.Authorization.RequiresModule(_1Rad.Domain.Constants.ModuleConstants.Ris)]
public class AppointmentScheduleController : ControllerBase
{
    private readonly IMediator _mediator;

    public AppointmentScheduleController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] DateTime? updatedAfter,
        [FromQuery] bool includeDeleted = false,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] int pageSize = 0,
        [FromQuery] string? cursor = null)
    {
        var result = await _mediator.Send(
            new GetAppointmentsQuery(search, status, updatedAfter, includeDeleted, startDate, pageSize, cursor));

        if (!result.IsPaged)
            return Ok(result.Items);

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

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAppointmentCommand command)
    {
        if (id != command.AppointmentId) return BadRequest("Appointment ID mismatch.");
        // ApprovedServiceRemoval is an INTERNAL flag set only by the approvals
        // review flow — never trust it off the wire, or a client could bypass the
        // paid-commission gate. Force it false for any public PUT.
        command = command with { ApprovedServiceRemoval = false };
        var result = await _mediator.Send(command);
        if (result.NotFound) return NotFound();
        // Ok in every other case — the body carries RequiresApproval /
        // RequiresRefundChoice (+ OverpayAmount) so the client can prompt accordingly.
        return Ok(result);
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] string status)
    {
        var result = await _mediator.Send(new UpdateAppointmentStatusCommand(id, status));
        if (!result.Success && result.Message == "Appointment not found.")
            return NotFound();
        return Ok(result);
    }

    // Scenario 05 — correct the "Referred By" so the commission credits the
    // right person. Applies immediately when nothing is paid; returns
    // requiresApproval (and applies nothing) once payment has been collected.
    public sealed record ChangeReferrerBody(
        string NewReferrerName, string? NewReferrerContact, bool? NewReferrerIsDoctor,
        string? NewReferrerSupportedByDoctor, string? NewReferrerEmail,
        string? NewReferrerSpecialty, string? NewReferrerDegree, string? NewReferrerAddress,
        string? NewReferrerSupportedSpecialty, string? NewReferrerSupportedDegree);

    [HttpPost("{id:guid}/change-referrer")]
    public async Task<IActionResult> ChangeReferrer(Guid id, [FromBody] ChangeReferrerBody body)
    {
        var result = await _mediator.Send(new ChangeReferrerCommand
        {
            AppointmentId = id,
            NewReferrerName = body?.NewReferrerName ?? string.Empty,
            NewReferrerContact = body?.NewReferrerContact,
            NewReferrerIsDoctor = body?.NewReferrerIsDoctor,
            NewReferrerSupportedByDoctor = body?.NewReferrerSupportedByDoctor,
            NewReferrerEmail = body?.NewReferrerEmail,
            NewReferrerSpecialty = body?.NewReferrerSpecialty,
            NewReferrerDegree = body?.NewReferrerDegree,
            NewReferrerAddress = body?.NewReferrerAddress,
            NewReferrerSupportedSpecialty = body?.NewReferrerSupportedSpecialty,
            NewReferrerSupportedDegree = body?.NewReferrerSupportedDegree,
        });
        return Ok(result);
    }
}
