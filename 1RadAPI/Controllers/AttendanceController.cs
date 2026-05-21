using _1Rad.Application.Features.Staff.Commands.UpsertAttendance;
using _1Rad.Application.Features.Staff.Queries.GetAttendance;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/attendance")]
public class AttendanceController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContext _userContext;

    public AttendanceController(IMediator mediator, IUserContext userContext)
    {
        _mediator = mediator;
        _userContext = userContext;
    }

    // GET /api/v1/attendance?month=YYYY-MM
    // Returns all attendance records for every staff member in the hospital for the given month.
    [HttpGet]
    public async Task<IActionResult> GetAttendance([FromQuery] string month)
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });
        if (string.IsNullOrWhiteSpace(month) || month.Length != 7 || month[4] != '-')
            return BadRequest(new { message = "month must be in YYYY-MM format." });

        var result = await _mediator.Send(new GetAttendanceQuery(hospitalId, month));
        return Ok(result);
    }

    // PUT /api/v1/attendance/{staffId}
    // Upsert a single day's attendance for a staff member.
    [HttpPut("{staffId:guid}")]
    public async Task<IActionResult> UpsertAttendance(Guid staffId, [FromBody] UpsertAttendanceRequest body)
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });

        var cmd = new UpsertAttendanceCommand(
            StaffId:          staffId,
            HospitalId:       hospitalId,
            MarkedByUserId:   _userContext.UserId,
            Date:             body.Date,
            Status:           body.Status,
            Note:             body.Note);

        var (attendanceId, error) = await _mediator.Send(cmd);
        return error == null
            ? Ok(new { attendanceId, message = "Attendance saved." })
            : BadRequest(new { message = error });
    }
}

public record UpsertAttendanceRequest(
    string Date,    // "YYYY-MM-DD"
    string Status,  // present | absent | halfday | late | leave
    string? Note);
