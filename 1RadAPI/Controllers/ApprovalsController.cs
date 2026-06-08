using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Domain.Constants;
using _1Rad.Application.Features.Approvals.Commands.CreateApprovalRequest;
using _1Rad.Application.Features.Approvals.Commands.ReviewApproval;
using _1Rad.Application.Features.Approvals.Queries.GetApprovals;
using _1Rad.Application.Features.Approvals.Queries.GetApprovalsCount;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/approvals")]
public class ApprovalsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ApprovalsController(IMediator mediator) => _mediator = mediator;

    // Any authenticated user can SUBMIT a request (the sensitive part is approving).
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApprovalRequestCommand command)
    {
        var id = await _mediator.Send(command);
        return Ok(new { id });
    }

    // The Approvals page lists pending requests (or ALL with ?status=ALL).
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status)
    {
        var result = await _mediator.Send(new GetApprovalsQuery { Status = status ?? "PENDING" });
        return Ok(result);
    }

    // Lightweight count for the nav badge — avoids fetching the whole list just
    // to read its length (polled per-admin).
    [HttpGet("count")]
    public async Task<IActionResult> Count([FromQuery] string? status)
    {
        var count = await _mediator.Send(new GetApprovalsCountQuery(status ?? "PENDING"));
        return Ok(new { count });
    }

    // Approve / reject — admin or admin-doctor only. Approving applies the change.
    [HttpPost("{id:guid}/review")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}")]
    public async Task<IActionResult> Review(Guid id, [FromBody] ReviewApprovalRequest body)
    {
        await _mediator.Send(new ReviewApprovalCommand { Id = id, Approve = body.Approve, Note = body.Note });
        return Ok(new { ok = true });
    }

    public record ReviewApprovalRequest(bool Approve, string? Note);
}
