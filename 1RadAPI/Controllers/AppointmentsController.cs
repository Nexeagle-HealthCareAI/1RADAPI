using _1Rad.Application.Features.Appointments.Queries.GetAppointments;
using _1Rad.Application.Features.Appointments.Queries.GetOverdueAppointments;
using _1Rad.Application.Features.Appointments.Queries.GetAppointmentComments;
using _1Rad.Application.Features.Appointments.Commands.AcknowledgeOverdue;
using _1Rad.Application.Features.Appointments.Commands.AddAppointmentComment;
using _1Rad.Application.Features.Appointments.Commands.CreateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;
using _1Rad.Application.Features.Appointments.Commands.ChangeReferrer;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentServiceStatus;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentServiceNotes;
using _1Rad.Application.Features.Appointments.Commands.ImportAppointments;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

using _1Rad.Application.Features.Appointments.Commands.UpdateReportProgress;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/appointments")]
[_1RadAPI.Authorization.RequiresModule(_1Rad.Domain.Constants.ModuleConstants.Ris)]
public class AppointmentsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IConfiguration _configuration;

    public AppointmentsController(IMediator mediator, IConfiguration configuration)
    {
        _mediator = mediator;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] DateTime? updatedAfter,
        [FromQuery] bool includeDeleted = false,
        [FromQuery] DateTime? startDate = null)
    {
        var result = await _mediator.Send(new GetAppointmentsQuery(search, status, updatedAfter, includeDeleted, startDate));
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

    // Scenario 05 — correct the "Referred By" so the commission credits the
    // right person. Applies immediately when nothing is paid; returns
    // requiresApproval (and applies nothing) once payment has been collected.
    public sealed record ChangeReferrerBody(string NewReferrerName, string? NewReferrerContact, bool? NewReferrerIsDoctor, string? NewReferrerSupportedByDoctor,
        string? NewReferrerEmail, string? NewReferrerSpecialty, string? NewReferrerDegree, string? NewReferrerAddress,
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
        {
            return NotFound(result);
        }
        if (!result.Success && result.NotAllowed)
        {
            return BadRequest(result);
        }
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
        {
            return NotFound(result);
        }
        return Ok(result);
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
        // RequiresRefundChoice (+ OverpayAmount) so the client can prompt the
        // operator (wallet vs cash) or surface the approval route before re-submitting.
        return Ok(result);
    }

    [HttpPut("{id}/operations-status")]
    public async Task<IActionResult> UpdateOperationsStatus(Guid id, [FromBody] UpdateReportProgressCommand command)
    {
        if (id != command.AppointmentId) return BadRequest("Appointment ID mismatch.");
        var result = await _mediator.Send(command);
        return result ? Ok(new { success = true }) : NotFound();
    }

    // Lists patients who arrived more than `thresholdMinutes` ago and have not
    // been delivered. The threshold defaults to Worklist:OverdueThresholdMinutes
    // in appsettings (180 = 3h) but accepts a query override for ops dashboards
    // that want a tighter SLA view. Returns longest-waiting first so the
    // bell-icon dropdown can show the most urgent at the top.
    [HttpGet("overdue")]
    public async Task<IActionResult> GetOverdue([FromQuery] int? thresholdMinutes)
    {
        var threshold = thresholdMinutes
            ?? _configuration.GetValue<int?>("Worklist:OverdueThresholdMinutes")
            ?? 180;
        var result = await _mediator.Send(new GetOverdueAppointmentsQuery(threshold));
        return Ok(new { thresholdMinutes = threshold, items = result });
    }

    // Silence the SLA bell for a specific appointment. Body: { acknowledged: bool }.
    // Acknowledged = true marks "I've seen this, stop alerting"; false reverts.
    // Idempotent: re-acking preserves the original acker + timestamp for audit.
    [HttpPost("{id:guid}/overdue-ack")]
    public async Task<IActionResult> AcknowledgeOverdue(Guid id, [FromBody] AcknowledgeOverdueBody body)
    {
        var ok = await _mediator.Send(new AcknowledgeOverdueCommand(id, body?.Acknowledged ?? true));
        return ok ? Ok(new { success = true }) : NotFound();
    }

    public record AcknowledgeOverdueBody(bool Acknowledged);

    // Append-only comment trail. POST adds, GET returns timeline newest-first.
    // Appointment.DelayReason is auto-updated to the latest comment server-
    // side so worklist rows can render the "current note" without a join.
    [HttpPost("{id:guid}/comments")]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddCommentBody body)
    {
        if (body == null || string.IsNullOrWhiteSpace(body.Body))
            return BadRequest("Comment body is required.");

        var result = await _mediator.Send(new AddAppointmentCommentCommand(id, body.Body));
        return result != null
            ? Ok(new { success = true, appointmentCommentId = result.AppointmentCommentId, createdAt = result.CreatedAt })
            : NotFound();
    }

    public record AddCommentBody(string Body);

    [HttpGet("{id:guid}/comments")]
    public async Task<IActionResult> GetComments(Guid id)
    {
        var items = await _mediator.Send(new GetAppointmentCommentsQuery(id));
        return Ok(new { success = true, items });
    }

    // Bulk fetch for the operations Excel export. POST (not GET) so the
    // appointmentIds list isn't URL-length-limited at ~2KB. Server caps at 500.
    [HttpPost("comments/bulk")]
    public async Task<IActionResult> GetCommentsBulk([FromBody] CommentsBulkBody body)
    {
        var ids = body?.AppointmentIds ?? new List<Guid>();
        var items = await _mediator.Send(new GetCommentsForAppointmentsQuery(ids));
        return Ok(new { success = true, items });
    }

    public record CommentsBulkBody(List<Guid> AppointmentIds);

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
