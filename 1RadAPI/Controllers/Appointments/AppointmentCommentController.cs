using _1Rad.Application.Features.Appointments.Queries.GetAppointmentComments;
using _1Rad.Application.Features.Appointments.Commands.AddAppointmentComment;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers.Appointments;

/// <summary>
/// Append-only appointment comment trail.
/// Isolated so comment features (e.g., mentions, attachments) can be
/// added without touching any scheduling or workflow controller.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/appointments")]
[_1RadAPI.Authorization.RequiresModule(_1Rad.Domain.Constants.ModuleConstants.Ris)]
public class AppointmentCommentController : ControllerBase
{
    private readonly IMediator _mediator;

    public AppointmentCommentController(IMediator mediator)
    {
        _mediator = mediator;
    }

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
            ? Ok(new
            {
                success = true,
                appointmentCommentId = result.AppointmentCommentId,
                createdAt = result.CreatedAt
            })
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
}
