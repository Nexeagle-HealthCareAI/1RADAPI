using _1Rad.Domain.Constants;
using _1Rad.Application.Features.Staff.Commands.ApplyLeave;
using _1Rad.Application.Features.Staff.Commands.ReviewLeave;
using _1Rad.Application.Features.Staff.Queries.GetLeaveRequests;
using _1Rad.Application.Features.Staff.LeaveBalances;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

// Gap analysis P1: the UI shows /staff only to admins; the API let any role edit pay and attendance.
[Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}")]
[ApiController]
[Route("api/v1/leave")]
public class LeaveController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContext _userContext;

    public LeaveController(IMediator mediator, IUserContext userContext)
    {
        _mediator = mediator;
        _userContext = userContext;
    }

    // GET /api/v1/leave                  → all leave requests for the hospital
    // GET /api/v1/leave?staffId=<guid>   → filtered by staff member
    [HttpGet]
    public async Task<IActionResult> GetLeaveRequests([FromQuery] Guid? staffId)
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });

        var result = await _mediator.Send(new GetLeaveRequestsQuery(hospitalId, staffId));
        return Ok(result);
    }

    // POST /api/v1/leave/{staffId}  → apply leave for a staff member
    [HttpPost("{staffId:guid}")]
    public async Task<IActionResult> ApplyLeave(Guid staffId, [FromBody] ApplyLeaveRequest body)
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });

        var cmd = new ApplyLeaveCommand(
            StaffId:    staffId,
            HospitalId: hospitalId,
            LeaveType:  body.LeaveType,
            FromDate:   body.FromDate,
            ToDate:     body.ToDate,
            Days:       body.Days,
            Reason:     body.Reason);

        var (leaveRequestId, error) = await _mediator.Send(cmd);
        return error == null
            ? Ok(new { leaveRequestId, message = "Leave request submitted." })
            : BadRequest(new { message = error });
    }

    // GET /api/v1/leave/balances?year=2026[&staffId=] → everyone's balance per leave type (server-side, Nov 2026)
    [HttpGet("balances")]
    public async Task<IActionResult> Balances([FromQuery] int? year, [FromQuery] Guid? staffId) =>
        Ok(await _mediator.Send(new GetLeaveBalancesQuery(year ?? DateTime.UtcNow.Year, staffId)));

    // POST /api/v1/leave/balances/{staffId}/adjust { year, leaveType, kind: OPENING|ADJUST, days, note }
    [HttpPost("balances/{staffId:guid}/adjust")]
    public async Task<IActionResult> Adjust(Guid staffId, [FromBody] AdjustLeaveRequest body)
    {
        await _mediator.Send(new AdjustLeaveBalanceCommand(staffId, body.Year, body.LeaveType, body.Kind, body.Days, body.Note));
        return Ok(new { success = true });
    }

    // POST /api/v1/leave/year-end { year } → carry what is left into the next year
    [HttpPost("year-end")]
    public async Task<IActionResult> YearEnd([FromBody] YearEndRequest body) =>
        Ok(await _mediator.Send(new RunLeaveYearEndCommand(body.Year)));

    // PATCH /api/v1/leave/{leaveRequestId}/status  → approve or reject
    [HttpPatch("{leaveRequestId:guid}/status")]
    public async Task<IActionResult> ReviewLeave(Guid leaveRequestId, [FromBody] ReviewLeaveRequest body)
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });

        var cmd = new ReviewLeaveCommand(
            LeaveRequestId:   leaveRequestId,
            HospitalId:       hospitalId,
            ReviewedByUserId: _userContext.UserId,
            Status:           body.Status);

        var (success, error) = await _mediator.Send(cmd);
        return success
            ? Ok(new { message = $"Leave request {body.Status}." })
            : BadRequest(new { message = error });
    }
}

public record ApplyLeaveRequest(
    string LeaveType,
    string FromDate,  // "YYYY-MM-DD"
    string ToDate,    // "YYYY-MM-DD"
    int Days,
    string? Reason);

public record ReviewLeaveRequest(string Status);  // approved | rejected

public record AdjustLeaveRequest(int Year, string LeaveType, string Kind, decimal Days, string? Note);
public record YearEndRequest(int Year);
