using _1Rad.Application.Features.Appointments.Queries.GetOverdueAppointments;
using _1Rad.Application.Features.Appointments.Commands.AcknowledgeOverdue;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace _1RadAPI.Controllers.Appointments;

/// <summary>
/// SLA and overdue appointment tracking.
/// Kept separate so the worklist alert system can evolve independently
/// from the core scheduling controller.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/appointments")]
[_1RadAPI.Authorization.RequiresModule(_1Rad.Domain.Constants.ModuleConstants.Ris)]
public class AppointmentSlaController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IConfiguration _configuration;

    public AppointmentSlaController(IMediator mediator, IConfiguration configuration)
    {
        _mediator = mediator;
        _configuration = configuration;
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

    // Silence the SLA bell for a specific appointment.
    // Acknowledged = true marks "I've seen this, stop alerting"; false reverts.
    // Idempotent: re-acking preserves the original acker + timestamp for audit.
    [HttpPost("{id:guid}/overdue-ack")]
    public async Task<IActionResult> AcknowledgeOverdue(
        Guid id, [FromBody] AcknowledgeOverdueBody body)
    {
        var ok = await _mediator.Send(
            new AcknowledgeOverdueCommand(id, body?.Acknowledged ?? true));
        return ok ? Ok(new { success = true }) : NotFound();
    }

    public record AcknowledgeOverdueBody(bool Acknowledged);
}
