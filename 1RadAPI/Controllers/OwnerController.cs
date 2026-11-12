using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Owner;
using _1Rad.Domain.Constants;

namespace _1RadAPI.Controllers;

/// <summary>
/// The owner board: all of the owner's centres on one screen. Owner only (admins):
/// the login must be an admin at the current centre, and every centre named in a
/// request is checked again against the database (OwnerAccess).
/// </summary>
[Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}")]
[ApiController]
[Route("api/v1/owner")]
public class OwnerController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IConfiguration _configuration;

    public OwnerController(IMediator mediator, IConfiguration configuration)
    {
        _mediator = mediator;
        _configuration = configuration;
    }

    // At a glance: every centre's money for the period, side by side, and what needs the owner.
    [HttpGet("glance")]
    public async Task<IActionResult> Glance([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var today = IstDateRange.ToIst(DateTime.UtcNow).Date;
        var late = _configuration.GetValue<int?>("Worklist:OverdueThresholdMinutes") ?? 180;
        return Ok(await _mediator.Send(new GetOwnerGlanceQuery(from ?? today, to ?? from ?? today, late)));
    }

    // Needs you: requests waiting for the owner at every centre.
    [HttpGet("approvals")]
    public async Task<IActionResult> Approvals() => Ok(await _mediator.Send(new GetOwnerApprovalsQuery()));

    [HttpPost("centres/{hospitalId:guid}/approvals/{id:guid}/review")]
    public async Task<IActionResult> Review(Guid hospitalId, Guid id, [FromBody] OwnerReviewRequest body)
    {
        await _mediator.Send(new OwnerReviewApprovalCommand(hospitalId, id, body.Approve, body.Note, body.RefundMode));
        return Ok(new { ok = true });
    }

    // Same setup everywhere: each centre's billing rules and prices, and which centre each one follows.
    [HttpGet("setup")]
    public async Task<IActionResult> Setup() => Ok(await _mediator.Send(new GetOwnerSetupQuery()));

    [HttpPost("setup/copy")]
    public async Task<IActionResult> Copy([FromBody] CopyOwnerSetupCommand body) => Ok(await _mediator.Send(body));

    [HttpDelete("setup/links/{hospitalId:guid}")]
    public async Task<IActionResult> Unlink(Guid hospitalId) => Ok(new { ok = await _mediator.Send(new UnlinkCentreCommand(hospitalId)) });

    public record OwnerReviewRequest(bool Approve, string? Note, string? RefundMode = null);
}
